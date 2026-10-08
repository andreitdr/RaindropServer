namespace RaindropServer.Channels.Text;

/// <summary>
/// The original text of a message that has been deleted, kept by an <see cref="IDeletedMessageStore"/>.
/// </summary>
/// <remarks>
/// Deleting a message erases its text from the message row (see <see cref="TextMessageRecord"/>), and
/// deleting a channel removes its messages outright. This is what is kept instead. It is written to
/// the database and nowhere else: no call reads it, and no client is ever sent it, so it is
/// there for the server owner and for nobody else.
/// </remarks>
public sealed record DeletedMessageRecord
{
    /// <summary>The id the message had. Also the key here: a message is preserved at most once.</summary>
    public required Guid MessageId { get; init; }

    /// <summary>The channel it was posted in — which may no longer exist.</summary>
    public required Guid ChannelId { get; init; }

    /// <summary>
    /// The channel's name at the time. Kept because the channel row itself goes when a whole channel is
    /// deleted, and an id alone says nothing to whoever is reading this later.
    /// </summary>
    public required string ChannelName { get; init; }

    /// <summary>The UUID of the member who wrote it.</summary>
    public required Guid AuthorId { get; init; }

    /// <summary>What was written.</summary>
    public required string Content { get; init; }

    /// <summary>When it was originally sent.</summary>
    public required DateTime SentAtUtc { get; init; }

    /// <summary>When it was deleted.</summary>
    public required DateTime DeletedAtUtc { get; init; }

    /// <summary>The UUID of whoever deleted it — the author, a moderator, or whoever deleted the channel.</summary>
    public required Guid DeletedBy { get; init; }

    /// <summary>
    /// Why it was deleted, one of <see cref="DeletedMessageReasons"/>. An open string, so a plugin that
    /// deletes messages for its own reasons can say so.
    /// </summary>
    public required string Reason { get; init; }
}

/// <summary>The reasons the server itself gives for deleting a message.</summary>
public static class DeletedMessageReasons
{
    /// <summary>A single message was deleted, by its author or by a moderator.</summary>
    public const string MessageDeleted = "message_deleted";

    /// <summary>The channel the message was in was deleted.</summary>
    public const string ChannelDeleted = "channel_deleted";
}
