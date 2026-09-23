namespace RaindropServer.Attachments;

/// <summary>
/// Persistence for attachment records — who uploaded what, how far it got, and which message it
/// belongs to. The bytes themselves live in an <see cref="IAttachmentStorage"/>.
/// </summary>
/// <remarks>
/// The state transitions are conditional updates, so a race between two requests settles in the
/// database: an attachment is claimed by one message, or completed once, however many try.
/// </remarks>
public interface IAttachmentStore
{
    Task InsertAsync(AttachmentRecord attachment, CancellationToken cancellationToken);

    Task<AttachmentRecord?> FindAsync(Guid attachmentId, CancellationToken cancellationToken);

    /// <summary>
    /// Records that <paramref name="receivedSize"/> bytes have arrived, only if the upload is still in
    /// progress and had exactly <paramref name="expectedReceivedSize"/> before. False otherwise.
    /// </summary>
    Task<bool> UpdateReceivedAsync(
        Guid attachmentId, long expectedReceivedSize, long receivedSize, CancellationToken cancellationToken);

    /// <summary>Moves an upload from uploading to uploaded, with its hash. False if it was not uploading.</summary>
    Task<bool> CompleteAsync(Guid attachmentId, byte[] ciphertextSha256, DateTime completedAtUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Attaches every one of <paramref name="attachmentIds"/> to <paramref name="messageId"/>, all or
    /// nothing: each must be uploaded, not yet attached, uploaded by <paramref name="uploaderId"/> for
    /// <paramref name="channelId"/>. False, with nothing changed, if any is not.
    /// </summary>
    Task<bool> ClaimAsync(
        IReadOnlyList<Guid> attachmentIds, Guid messageId, Guid uploaderId, Guid channelId, CancellationToken cancellationToken);

    /// <summary>Undoes <see cref="ClaimAsync"/> for a message that was never stored.</summary>
    Task ReleaseAsync(Guid messageId, CancellationToken cancellationToken);

    /// <summary>The attachments of these messages that are still attached.</summary>
    Task<IReadOnlyList<AttachmentRecord>> ListForMessagesAsync(
        IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken);

    /// <summary>How many uploads of this member's are not yet part of a message.</summary>
    Task<int> CountPendingAsync(Guid uploaderId, CancellationToken cancellationToken);

    /// <summary>Marks one attachment deleted, whatever state it was in.</summary>
    Task MarkDeletedAsync(Guid attachmentId, DateTime deletedAtUtc, CancellationToken cancellationToken);

    /// <summary>Marks every attachment of a message deleted.</summary>
    Task MarkMessageDeletedAsync(Guid messageId, DateTime deletedAtUtc, CancellationToken cancellationToken);

    /// <summary>Marks every attachment uploaded for a channel deleted.</summary>
    Task MarkChannelDeletedAsync(Guid channelId, DateTime deletedAtUtc, CancellationToken cancellationToken);

    /// <summary>Marks deleted every upload not attached to a message and created before <paramref name="createdBeforeUtc"/>.</summary>
    Task<int> ExpirePendingAsync(DateTime createdBeforeUtc, DateTime deletedAtUtc, CancellationToken cancellationToken);

    /// <summary>Up to <paramref name="limit"/> deleted attachments whose files are still to be removed.</summary>
    Task<IReadOnlyList<AttachmentRecord>> ListDeletedAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Forgets an attachment entirely, once its file is gone.</summary>
    Task RemoveAsync(Guid attachmentId, CancellationToken cancellationToken);

    /// <summary>Counts for the owner's console.</summary>
    Task<AttachmentStatistics> GetStatisticsAsync(CancellationToken cancellationToken);
}

/// <param name="Attached">Files that are part of a message.</param>
/// <param name="AttachedBytes">Their ciphertext, in bytes.</param>
/// <param name="Pending">Uploads in progress or waiting to be sent.</param>
/// <param name="PendingBytes">Their declared ciphertext, in bytes.</param>
/// <param name="AwaitingRemoval">Deleted attachments whose files have not been removed yet.</param>
public sealed record AttachmentStatistics(long Attached, long AttachedBytes, long Pending, long PendingBytes, long AwaitingRemoval);
