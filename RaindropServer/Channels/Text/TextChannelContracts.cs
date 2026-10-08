namespace RaindropServer.Channels.Text;

// ── Shared shapes ────────────────────────────────────────────────────────────

/// <summary>
/// A message as a client sees it. A deleted message still appears, with no content and
/// <see cref="Deleted"/> set, because it keeps its place in the conversation and its link to the
/// author's UUID.
/// </summary>
/// <param name="MessageId">The id to pass back when deleting it.</param>
/// <param name="ChannelId">The text channel it belongs to.</param>
/// <param name="AuthorId">The UUID of whoever wrote it — resolve it with <c>UserService.LookupUser</c>.</param>
/// <param name="Content">The text, or null once the message has been deleted.</param>
/// <param name="SentAtUtc">When the server accepted it; what the ordering is by.</param>
/// <param name="Deleted">Whether this is a tombstone the client should render as a removed message.</param>
/// <param name="DeletedAtUtc">When it was deleted, if it was.</param>
/// <param name="DeletedBy">The UUID of whoever deleted it: the author, or a moderator of the channel.</param>
/// <remarks>
/// An end-to-end encrypted message has no <c>Content</c>: it carries <c>Ciphertext</c> (the MLS
/// application message, which only the channel group's members can decrypt), the <c>Epoch</c> it was
/// sent in and the <c>SenderDeviceId</c>. A plaintext message leaves those three null.
/// <para>
/// <c>Attachments</c> lists the encrypted files the message carries, by id — their names, types and
/// keys are inside the ciphertext. Null for a message without files, and for a deleted one.
/// </para>
/// </remarks>
public record ChannelMessage(
    Guid MessageId,
    Guid ChannelId,
    Guid AuthorId,
    string? Content,
    DateTime SentAtUtc,
    bool Deleted,
    DateTime? DeletedAtUtc,
    Guid? DeletedBy,
    byte[]? Ciphertext = null,
    long? Epoch = null,
    Guid? SenderDeviceId = null,
    IReadOnlyList<MessageAttachment>? Attachments = null);

/// <summary>An encrypted file a message carries, as far as the server can describe it.</summary>
/// <param name="AttachmentId">What to pass to <c>AttachmentService.Download</c>.</param>
/// <param name="Kind"><c>file</c>, or <c>thumbnail</c> for a preview of another file in the same message.</param>
/// <param name="Extension">
/// The extension the sender declared, lower case without the dot; empty for none. A client compares it
/// with the name inside the encrypted message, and refuses to preview a file whose two disagree.
/// </param>
/// <param name="CiphertextSize">Bytes to download.</param>
/// <param name="CiphertextSha256">
/// SHA-256 of the encrypted file as it was uploaded. The hash that counts is the one inside the
/// encrypted message, which the server cannot change; this one is for checking a download early.
/// </param>
public record MessageAttachment(
    Guid AttachmentId, string Kind, string Extension, long CiphertextSize, byte[]? CiphertextSha256 = null);

// ── Requests ─────────────────────────────────────────────────────────────────

/// <param name="ChannelId">The text channel to post in.</param>
/// <param name="Content">The message text.</param>
public record SendMessageRequest(Guid ChannelId, string Content);

/// <param name="ChannelId">The text channel to post in.</param>
/// <param name="Message">
/// The MLS application message (<c>PrivateMessage</c>) the sender's device encrypted for the
/// channel's group, in its current epoch.
/// </param>
/// <param name="AttachmentIds">
/// Uploads to send with the message, finished with <c>AttachmentService.CompleteUpload</c>, in
/// the order the message lists them. Each must have been uploaded by the caller for this channel.
/// </param>
public record SendEncryptedMessageRequest(Guid ChannelId, byte[] Message, IReadOnlyList<Guid>? AttachmentIds = null);

/// <param name="ChannelId">The text channel to read.</param>
/// <param name="Before">
/// The <c>NextCursor</c> from the previous page, or null for the newest messages.
/// </param>
/// <param name="Limit">
/// How many messages to return. Left out, the server's default page size is used; more than the
/// server's maximum is trimmed down to it.
/// </param>
public record MessageHistoryRequest(Guid ChannelId, string? Before = null, int? Limit = null);

/// <param name="MessageId">The message to delete.</param>
public record DeleteMessageRequest(Guid MessageId);

// ── Responses ────────────────────────────────────────────────────────────────

/// <summary>One page of a channel's history.</summary>
/// <param name="ChannelId">The channel that was read.</param>
/// <param name="Messages">
/// The page, in chronological order — oldest first, as it should be rendered. Paging itself runs
/// backwards: the first page is the newest messages, and each <paramref name="NextCursor"/> leads
/// further into the past.
/// </param>
/// <param name="NextCursor">
/// Pass this back as <see cref="MessageHistoryRequest.Before"/> for the page before this one. Null
/// once the beginning of the channel has been reached.
/// </param>
public record MessageHistoryResponse(Guid ChannelId, IReadOnlyList<ChannelMessage> Messages, string? NextCursor);
