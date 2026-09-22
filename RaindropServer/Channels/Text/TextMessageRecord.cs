namespace RaindropServer.Channels.Text;

/// <summary>
/// One message in a text channel, as persisted by an <see cref="ITextMessageStore"/>.
/// </summary>
/// <remarks>
/// A deleted message keeps its row: the text goes, the link to the author's UUID stays. Messages are
/// tied to a UUID and that tie is not something anyone can remove, so deletion is a tombstone rather
/// than a <c>DELETE</c> — the same shape the user records use when an account is erased. It also
/// keeps history paging honest, since the row a client's cursor points at cannot vanish underneath it.
/// </remarks>
public sealed record TextMessageRecord
{
    /// <summary>The server-generated id of this message.</summary>
    public required Guid MessageId { get; init; }

    /// <summary>The text channel it was posted in.</summary>
    public required Guid ChannelId { get; init; }

    /// <summary>The UUID of the member who wrote it. Never changes, and never becomes null.</summary>
    public required Guid AuthorId { get; init; }

    /// <summary>What was written. Null once the message has been deleted.</summary>
    public string? Content { get; init; }

    /// <summary>When the server accepted it. This is what "chronological order" is ordered by.</summary>
    public required DateTime SentAtUtc { get; init; }

    /// <summary>When it was deleted, if it was.</summary>
    public DateTime? DeletedAtUtc { get; init; }

    /// <summary>
    /// The UUID of whoever deleted it — the author themselves, or a member with permission to
    /// moderate the channel.
    /// </summary>
    public Guid? DeletedBy { get; init; }

    /// <summary>Whether this row is a tombstone rather than a readable message.</summary>
    public bool IsDeleted => DeletedAtUtc is not null;

    /// <summary>
    /// For an end-to-end encrypted message: the MLS application message (a <c>PrivateMessage</c>)
    /// exactly as the sender's device produced it. The server cannot read it. Null for a plaintext
    /// message, and once the message is deleted.
    /// </summary>
    public byte[]? Ciphertext { get; init; }

    /// <summary>The MLS epoch an encrypted message was sent in; null for a plaintext one.</summary>
    public long? Epoch { get; init; }

    /// <summary>Which of the author's devices sent an encrypted message; null for a plaintext one.</summary>
    public Guid? SenderDeviceId { get; init; }

    /// <summary>Whether this is an end-to-end encrypted message, deleted or not.</summary>
    public bool IsEncrypted => Epoch is not null;

    /// <summary>
    /// Erases the text while keeping the row, the author and the position in the conversation.
    /// </summary>
    public TextMessageRecord AsDeleted(Guid deletedBy, DateTime utcNow) => this with
    {
        Content = null,
        Ciphertext = null,
        DeletedAtUtc = utcNow,
        DeletedBy = deletedBy
    };
}
