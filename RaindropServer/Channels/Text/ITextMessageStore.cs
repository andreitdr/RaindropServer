namespace RaindropServer.Channels.Text;

/// <summary>
/// Persistence for the messages in text channels. Implemented once per database module, next to
/// <see cref="Users.IUserStore"/> and <see cref="IChannelStore"/>.
/// </summary>
public interface ITextMessageStore
{
    /// <summary>Finds a message by its id, or null when there is no such message.</summary>
    Task<TextMessageRecord?> FindByIdAsync(Guid messageId, CancellationToken cancellationToken);

    /// <summary>Appends a message to its channel.</summary>
    Task InsertAsync(TextMessageRecord message, CancellationToken cancellationToken);

    /// <summary>
    /// Overwrites an existing message row, matched on <see cref="TextMessageRecord.MessageId"/>.
    /// This is how a deletion is written, since deleting keeps the row.
    /// </summary>
    Task UpdateAsync(TextMessageRecord message, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one page of a channel's history, newest first, starting just before
    /// <paramref name="before"/> — or at the newest message when it is null. Tombstones are included:
    /// a deleted message still occupies its place in the conversation.
    /// </summary>
    /// <param name="channelId">The channel to read.</param>
    /// <param name="before">Where the previous page ended, or null to start at the newest message.</param>
    /// <param name="limit">The most messages to return.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<TextMessageRecord>> ReadHistoryAsync(
        Guid channelId, MessageCursor? before, int limit, CancellationToken cancellationToken);

    /// <summary>Removes every message in a channel, for when the channel itself is deleted.</summary>
    Task DeleteChannelMessagesAsync(Guid channelId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes every plaintext message — everything posted before the server required end-to-end
    /// encryption — and returns how many. Encrypted messages are untouched. The owner console's
    /// <c>purge-plaintext</c>; a store that predates encryption refuses.
    /// </summary>
    Task<int> PurgePlaintextAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{GetType().Name} cannot purge plaintext messages.");
}
