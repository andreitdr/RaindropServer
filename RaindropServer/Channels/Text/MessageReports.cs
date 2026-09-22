using System.Buffers.Binary;
using System.Text;
using RaindropServer.E2ee;
using RaindropServer.Events;
using RaindropServer.Roles;
using RaindropServer.Security;
using RaindropServer.Users;

namespace RaindropServer.Channels.Text;

/// <summary>A reported message, as moderators see it.</summary>
/// <param name="Body">
/// The text the reporter revealed. Proven to be what the author's device wrote: its attestation
/// verified against the device's registered key when the report was made.
/// </param>
public sealed record MessageReportRecord(
    Guid ReportId,
    Guid MessageId,
    Guid ChannelId,
    Guid AuthorId,
    Guid SenderDeviceId,
    Guid ReporterId,
    string Body,
    string? Reason,
    DateTime MessageSentAtUtc,
    DateTime ReportedAtUtc);

/// <summary>Persistence for message reports.</summary>
public interface IMessageReportStore
{
    Task AddAsync(MessageReportRecord report, CancellationToken cancellationToken);

    /// <summary>A channel's reports, newest first.</summary>
    Task<IReadOnlyList<MessageReportRecord>> ListForChannelAsync(Guid channelId, int limit, CancellationToken cancellationToken);

    /// <summary>Every channel's reports, newest first — for the owner console.</summary>
    Task<IReadOnlyList<MessageReportRecord>> ListRecentAsync(int limit, CancellationToken cancellationToken);
}

/// <param name="MessageId">The encrypted message being reported.</param>
/// <param name="Body">Its decrypted text, which the reporter chooses to reveal.</param>
/// <param name="ClientMessageId">From the message's payload, as the sender signed it.</param>
/// <param name="ClientSentAtUnixMs">From the message's payload, as the sender signed it.</param>
/// <param name="Type">The payload type, as the sender signed it (<c>text</c>).</param>
/// <param name="Attestation">The sender device's Ed25519 signature over the above.</param>
/// <param name="Reason">Why it is being reported; optional, at most 500 characters.</param>
public record ReportMessageRequest(
    Guid MessageId,
    string Body,
    string ClientMessageId,
    long ClientSentAtUnixMs,
    string Type,
    byte[] Attestation,
    string? Reason);

public record MessageReportSummary(
    Guid ReportId,
    Guid MessageId,
    Guid ChannelId,
    Guid AuthorId,
    Guid SenderDeviceId,
    Guid ReporterId,
    string Body,
    string? Reason,
    DateTime MessageSentAtUtc,
    DateTime ReportedAtUtc);

/// <param name="ChannelId">The channel whose reports to list.</param>
public record ListReportsRequest(Guid ChannelId);

public record ListReportsResponse(IReadOnlyList<MessageReportSummary> Reports);

/// <summary>
/// Reporting end-to-end encrypted messages. The server cannot read a message, so a report brings
/// the text with it, together with the <em>attestation</em> the author's device put inside the
/// encrypted payload: its Ed25519 signature over the channel, the client's message id and send
/// time, the type and the body. The server checks that signature against the key of the device
/// the message came from (<c>raindrop_devices</c>). A reporter can therefore reveal a message, but
/// cannot invent one or change its words.
/// </summary>
/// <remarks>
/// What an attestation proves is authorship: that device wrote those words in that channel. It
/// does not tie them to the exact stored ciphertext, which only group members can decrypt; a
/// reporter could present a different message the same device genuinely sent in the same channel.
/// </remarks>
public sealed class MessageReportService
{
    private static readonly byte[] Context = "raindrop/message-attestation/v1\n"u8.ToArray();
    private const int MaximumReasonLength = 500;

    /// <summary>Sent to members with <c>manage_messages</c> in the channel when a message is reported.</summary>
    public const string MessageReported = "message.reported";

    private readonly ChannelService _channels;
    private readonly ITextMessageStore _messages;
    private readonly IDeviceStore _devices;
    private readonly IMessageReportStore _reports;
    private readonly IPermissionResolver _permissions;
    private readonly IEventPublisher _events;
    private readonly ChannelOptions _options;
    private readonly TimeProvider _time;

    public MessageReportService(
        ChannelService channels,
        ITextMessageStore messages,
        IDeviceStore devices,
        IMessageReportStore reports,
        IPermissionResolver permissions,
        IEventPublisher events,
        ChannelOptions options,
        TimeProvider? time = null)
    {
        _channels = channels;
        _messages = messages;
        _devices = devices;
        _reports = reports;
        _permissions = permissions;
        _events = events;
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Reports an encrypted message, proving its text with the author's attestation.</summary>
    public async Task<MessageReportSummary> ReportAsync(
        UserSession session, ReportMessageRequest request, CancellationToken cancellationToken)
    {
        var message = request.MessageId == Guid.Empty
            ? null
            : await _messages.FindByIdAsync(request.MessageId, cancellationToken);
        if (message is null)
            throw ApiException.NotFound("message_not_found", $"There is no message {request.MessageId} on this server.");

        // Also the check that the reporter can see the channel at all.
        var access = await _channels.ResolveAsync(session.UserId, message.ChannelId, ChannelType.Text, cancellationToken);

        if (!message.IsEncrypted || message.SenderDeviceId is not { } deviceId)
            throw ApiException.BadRequest("not_encrypted",
                "That message is not end-to-end encrypted; the server already has its text.");
        if (string.IsNullOrEmpty(request.Body) || request.Body.Length > _options.MaximumMessageLength)
            throw ApiException.BadRequest("invalid_message", "The reported text is empty or too long.");
        if (request.Reason is { Length: > MaximumReasonLength })
            throw ApiException.BadRequest("invalid_reason", $"A reason is at most {MaximumReasonLength} characters.");

        // The device the message came from, even if it has since been revoked: it held that key
        // when it wrote the message.
        var device = await _devices.FindDeviceAsync(message.AuthorId, deviceId, cancellationToken);
        var signed = Canonical(message.ChannelId, request.ClientMessageId ?? "", request.ClientSentAtUnixMs,
            request.Type ?? "", request.Body);
        if (device is null || !Ed25519Signatures.Verify(device.SignatureKey, signed, request.Attestation ?? []))
            throw ApiException.BadRequest("invalid_attestation",
                "That text is not proven to be what the author's device wrote.");

        var report = new MessageReportRecord(
            Guid.NewGuid(), message.MessageId, message.ChannelId, message.AuthorId, deviceId, session.UserId,
            request.Body, string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
            message.SentAtUtc, _time.GetUtcNow().UtcDateTime);
        await _reports.AddAsync(report, cancellationToken);

        var summary = ToSummary(report);
        await _events.PublishAsync(MessageReported, summary, ModeratorsOf(access.Channel));
        return summary;
    }

    /// <summary>A channel's reports. Needs <c>manage_messages</c> there.</summary>
    public async Task<ListReportsResponse> ListAsync(
        UserSession session, ListReportsRequest request, CancellationToken cancellationToken)
    {
        var access = await _channels.ResolveAsync(session.UserId, request.ChannelId, ChannelType.Text, cancellationToken);
        access.Permissions.Require(Permission.ManageMessages, "review reported messages here");

        var reports = await _reports.ListForChannelAsync(access.Channel.ChannelId, 200, cancellationToken);
        return new ListReportsResponse(reports.Select(ToSummary).ToList());
    }

    /// <summary>
    /// The bytes an attestation signs: the context, then the channel id (lower case), the client
    /// message id, the send time in ms, the type and the body — each text field a 4-byte big-endian
    /// length followed by its UTF-8. Must match <c>MessagePayload.attestationBytes</c> on clients.
    /// </summary>
    public static byte[] Canonical(Guid channelId, string clientMessageId, long sentAtUnixMs, string type, string body)
    {
        var output = new List<byte>(Context);
        Field(output, channelId.ToString("D"));
        Field(output, clientMessageId);
        var time = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(time, sentAtUnixMs);
        output.AddRange(time);
        Field(output, type);
        Field(output, body);
        return output.ToArray();
    }

    private static void Field(List<byte> output, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)bytes.Length);
        output.AddRange(length);
        output.AddRange(bytes);
    }

    private EventAudience ModeratorsOf(ChannelRecord channel) =>
        async (userId, cancellationToken) =>
            (await _permissions.GetAsync(userId, channel, cancellationToken)).Allows(Permission.ManageMessages);

    private static MessageReportSummary ToSummary(MessageReportRecord r) => new(
        r.ReportId, r.MessageId, r.ChannelId, r.AuthorId, r.SenderDeviceId, r.ReporterId, r.Body, r.Reason,
        r.MessageSentAtUtc, r.ReportedAtUtc);
}
