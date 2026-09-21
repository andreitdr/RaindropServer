using RaindropServer.Events;
using RaindropServer.Roles;
using RaindropServer.Users;

namespace RaindropServer.Channels.Text;

/// <summary>
/// What happens inside a text channel: posting a message, reading the history, and deleting a
/// message. Every rule lives here, so the endpoints stay thin and the database driver stays dumb.
/// </summary>
/// <remarks>
/// The channel itself — whether it exists, and whether the caller may see it — is
/// <see cref="ChannelService"/>'s business, reached through
/// <see cref="ChannelService.ResolveAsync"/>. This service only ever deals with text channels, and
/// says so on every call, so pointing it at a voice channel is refused rather than half-honoured.
/// </remarks>
public sealed class TextChannelService
{
    private readonly ChannelService _channels;
    private readonly ITextMessageStore _messages;
    private readonly ChannelOptions _options;
    private readonly IEventPublisher _events;
    private readonly IReadOnlyList<IMessageDeletionListener> _deletionListeners;
    private readonly TimeProvider _time;

    public TextChannelService(
        ChannelService channels,
        ITextMessageStore messages,
        ChannelOptions options,
        IEventPublisher events,
        IEnumerable<IMessageDeletionListener> deletionListeners,
        TimeProvider? time = null)
    {
        _channels = channels;
        _messages = messages;
        _options = options;
        _events = events;
        _deletionListeners = deletionListeners.ToList();
        _time = time ?? TimeProvider.System;
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    /// <summary>Posts a message to a text channel on behalf of the caller.</summary>
    public async Task<ChannelMessage> SendAsync(
        UserSession session, SendMessageRequest request, CancellationToken cancellationToken)
    {
        var access = await _channels.ResolveAsync(session.UserId, request.ChannelId, ChannelType.Text, cancellationToken);
        access.Permissions.Require(Permission.SendMessages, "post in this channel");

        var content = ValidateContent(request.Content);

        var message = new TextMessageRecord
        {
            MessageId = Guid.NewGuid(),
            ChannelId = access.Channel.ChannelId,
            AuthorId = session.UserId,
            Content = content,
            SentAtUtc = UtcNow
        };

        await _messages.InsertAsync(message, cancellationToken);

        var sent = ToContract(message);
        await _events.PublishAsync(ServerEventTypes.MessageCreated, sent, _channels.AudienceFor(access.Channel));

        return sent;
    }

    /// <summary>
    /// Reads one page of the channel, newest page first. The page itself comes back in chronological
    /// order; following <see cref="MessageHistoryResponse.NextCursor"/> walks further into the past
    /// until it comes back null.
    /// </summary>
    public async Task<MessageHistoryResponse> ReadHistoryAsync(
        UserSession session, MessageHistoryRequest request, CancellationToken cancellationToken)
    {
        var access = await _channels.ResolveAsync(session.UserId, request.ChannelId, ChannelType.Text, cancellationToken);
        var before = MessageCursor.Decode(request.Before);
        var limit = PageSize(request.Limit);

        // One more than asked for: the extra row is how we know there is a page beyond this one
        // without the client having to make a request that comes back empty.
        var page = await _messages.ReadHistoryAsync(access.Channel.ChannelId, before, limit + 1, cancellationToken);

        var hasMore = page.Count > limit;
        var messages = hasMore ? page.Take(limit).ToList() : page.ToList();

        // The store reads newest first so that paging can start from the end; a client renders the
        // conversation the other way round.
        var oldest = messages.Count > 0 ? messages[^1] : null;
        messages.Reverse();

        return new MessageHistoryResponse(
            access.Channel.ChannelId,
            messages.Select(ToContract).ToList(),
            hasMore && oldest is not null ? MessageCursor.Of(oldest).Encode() : null);
    }

    /// <summary>
    /// Deletes a message. The author may always delete their own, whatever permissions they hold;
    /// deleting anybody else's needs <see cref="Permission.ManageMessages"/> in that channel.
    /// The row survives either way — the text is what goes, not the message's place in the
    /// conversation or its link to the author's UUID.
    /// </summary>
    /// <remarks>
    /// The text is handed to every <see cref="IMessageDeletionListener"/> first, which is how a copy
    /// is kept for the server owner. A listener that throws stops the deletion with nothing changed.
    /// The order — listeners, then the write, then the event — means a failure part-way can leave a
    /// preserved copy of a message that is still there, and never a deleted message with no copy.
    /// </remarks>
    public async Task<ChannelMessage> DeleteAsync(
        UserSession session, DeleteMessageRequest request, CancellationToken cancellationToken)
    {
        var message = request.MessageId == Guid.Empty
            ? null
            : await _messages.FindByIdAsync(request.MessageId, cancellationToken);

        if (message is null)
            throw ApiException.NotFound("message_not_found", $"There is no message {request.MessageId} on this server.");

        // Resolving the channel is also the permission check, and answers 404 for a channel the
        // caller cannot see — so a message in one cannot be probed for either.
        var access = await _channels.ResolveAsync(session.UserId, message.ChannelId, ChannelType.Text, cancellationToken);

        if (message.AuthorId != session.UserId)
            access.Permissions.Require(Permission.ManageMessages, "delete other members' messages here");

        // Deleting an already-deleted message is not an error: the caller wanted it gone, and it is.
        if (message.IsDeleted)
            return ToContract(message);

        var deletedAt = UtcNow;

        // Before the text goes, while it can still be read. Not swallowed on failure, unlike other
        // listeners: see IMessageDeletionListener.
        var deletion = new MessageDeletion(message, access.Channel, session.UserId, deletedAt);
        foreach (var listener in _deletionListeners)
            await listener.OnMessageDeletingAsync(deletion, cancellationToken);

        var deleted = message.AsDeleted(session.UserId, deletedAt);
        await _messages.UpdateAsync(deleted, cancellationToken);

        var tombstone = ToContract(deleted);
        await _events.PublishAsync(ServerEventTypes.MessageDeleted, tombstone, _channels.AudienceFor(access.Channel));

        return tombstone;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string ValidateContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw ApiException.BadRequest("invalid_message", "A message cannot be empty.");

        var trimmed = content.Trim();

        if (trimmed.Length > _options.MaximumMessageLength)
            throw ApiException.BadRequest("message_too_long",
                $"A message may be at most {_options.MaximumMessageLength} characters long.");

        return trimmed;
    }

    private int PageSize(int? requested) => requested switch
    {
        null or <= 0 => _options.DefaultHistoryPageSize,
        int n => Math.Min(n, _options.MaximumHistoryPageSize)
    };

    private static ChannelMessage ToContract(TextMessageRecord message) => new(
        message.MessageId,
        message.ChannelId,
        message.AuthorId,
        message.Content,
        message.SentAtUtc,
        message.IsDeleted,
        message.DeletedAtUtc,
        message.DeletedBy);
}
