namespace RaindropServer.Attachments;

/// <summary>Where an attachment is in its life.</summary>
public enum AttachmentState
{
    /// <summary>Created; its bytes are still arriving.</summary>
    Uploading,

    /// <summary>Every byte has arrived and matched its hash; waiting to be sent in a message.</summary>
    Uploaded,

    /// <summary>Part of a message, and readable by everybody who can see that message's channel.</summary>
    Attached,

    /// <summary>Its message, channel or upload is gone; the file is removed by the next maintenance pass.</summary>
    Deleted
}

/// <summary>The names attachment states and kinds are stored and sent under.</summary>
public static class AttachmentNames
{
    /// <summary>A file a member added to a message.</summary>
    public const string FileKind = "file";

    /// <summary>
    /// A small preview image of another file in the same message, made by the sender, since the server
    /// cannot make one from a file it cannot read.
    /// </summary>
    public const string ThumbnailKind = "thumbnail";

    /// <summary>The kinds this build accepts. An open string on the wire, so a later build can add one.</summary>
    public static IReadOnlyList<string> Kinds { get; } = [FileKind, ThumbnailKind];

    public static string Of(AttachmentState state) => state switch
    {
        AttachmentState.Uploading => "uploading",
        AttachmentState.Uploaded => "uploaded",
        AttachmentState.Attached => "attached",
        _ => "deleted"
    };

    public static AttachmentState ParseState(string name) => name switch
    {
        "uploading" => AttachmentState.Uploading,
        "uploaded" => AttachmentState.Uploaded,
        "attached" => AttachmentState.Attached,
        _ => AttachmentState.Deleted
    };
}

/// <summary>
/// One encrypted file, as persisted by an <see cref="IAttachmentStore"/>. The server knows who
/// uploaded it, to which channel, its declared extension and its size; its name, type and contents are
/// inside the encryption, carried by the message that shares it.
/// </summary>
public sealed record AttachmentRecord
{
    public required Guid AttachmentId { get; init; }

    /// <summary>The text channel it was uploaded for. It can only ever be attached to a message there.</summary>
    public required Guid ChannelId { get; init; }

    public required Guid UploaderId { get; init; }

    /// <summary>The device whose bound session uploaded it.</summary>
    public required Guid UploaderDeviceId { get; init; }

    /// <summary><see cref="AttachmentNames.FileKind"/> or <see cref="AttachmentNames.ThumbnailKind"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>The extension the uploader declared, lower case without the dot; empty for none.</summary>
    public required string Extension { get; init; }

    /// <summary>The ciphertext's length, declared up front and fixed from then on.</summary>
    public required long CiphertextSize { get; init; }

    /// <summary>How many bytes of ciphertext have arrived so far.</summary>
    public long ReceivedSize { get; init; }

    /// <summary>SHA-256 of the whole ciphertext, once the upload is complete.</summary>
    public byte[]? CiphertextSha256 { get; init; }

    public required AttachmentState State { get; init; }

    /// <summary>The message it was sent in, once it has been.</summary>
    public Guid? MessageId { get; init; }

    public required DateTime CreatedAtUtc { get; init; }

    public DateTime? CompletedAtUtc { get; init; }

    public DateTime? DeletedAtUtc { get; init; }

    /// <summary>The plaintext size, derived from the ciphertext's.</summary>
    public long PlaintextSize => AttachmentFormat.PlaintextLength(CiphertextSize) ?? 0;

    /// <summary>What the file is called in an <see cref="IAttachmentStorage"/>.</summary>
    public string StorageKey => StorageKeyFor(AttachmentId);

    public static string StorageKeyFor(Guid attachmentId) => attachmentId.ToString("N");
}
