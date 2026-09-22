namespace RaindropServer.Channels.Text;

/// <summary>
/// Persistence for the original text of deleted messages. Implemented once per database module, next
/// to <see cref="ITextMessageStore"/>.
/// </summary>
/// <remarks>
/// Both operations are idempotent — a message that is already preserved is left as it was — because a
/// deletion that fails after preserving is retried from the start, and must not preserve twice or
/// overwrite who deleted it first.
/// </remarks>
public interface IDeletedMessageStore
{
    /// <summary>Preserves one message. Does nothing if that message is already preserved.</summary>
    Task ArchiveMessageAsync(DeletedMessageRecord message, CancellationToken cancellationToken);

    /// <summary>
    /// Preserves every message still readable in a channel about to be deleted, in one statement.
    /// Tombstones are skipped — their text was preserved when they were deleted, or is already gone —
    /// and so is anything already preserved.
    /// </summary>
    Task ArchiveChannelMessagesAsync(ChannelDeletion deletion, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the whole archive of deleted messages' text and returns how many rows went. Part of the
    /// owner console's <c>purge-plaintext</c>; a store that predates it refuses.
    /// </summary>
    Task<int> PurgeAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{GetType().Name} cannot purge the archive.");
}
