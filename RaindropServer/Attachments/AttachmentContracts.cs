namespace RaindropServer.Attachments;

// ── Requests ─────────────────────────────────────────────────────────────────

/// <param name="ChannelId">The text channel the file will be sent in; it cannot be sent anywhere else.</param>
/// <param name="Extension">
/// The file's extension, without the dot; empty or null for none. Lower-cased by the server. This and
/// the size are the only things about the file the server learns.
/// </param>
/// <param name="CiphertextSize">
/// The length of the encrypted file, which fixes the plaintext length exactly — the size limits are
/// applied to that.
/// </param>
/// <param name="Kind"><c>file</c> (the default), or <c>thumbnail</c> for a preview of another file.</param>
public record CreateUploadRequest(Guid ChannelId, string? Extension, long CiphertextSize, string? Kind = null);

/// <param name="AttachmentId">The upload being asked about.</param>
public record UploadReference(Guid AttachmentId);

/// <param name="AttachmentId">The upload to finish.</param>
/// <param name="CiphertextSha256">SHA-256 of the whole encrypted file, which the stored bytes must match.</param>
public record CompleteUploadRequest(Guid AttachmentId, byte[] CiphertextSha256);

// ── Responses ────────────────────────────────────────────────────────────────

/// <summary>What this server accepts, so a client can refuse a file before uploading any of it.</summary>
/// <param name="Enabled">Whether files can be added to messages here at all.</param>
/// <param name="MaximumFileBytes">The largest file, in bytes of plaintext, unless its extension has its own limit.</param>
/// <param name="MaximumFileBytesByExtension">Limits for particular extensions.</param>
/// <param name="AllowedExtensions">The only extensions accepted, or null for any that is not blocked.</param>
/// <param name="BlockedExtensions">Extensions never accepted.</param>
/// <param name="AllowFilesWithoutExtension">Whether a file with no extension may be uploaded.</param>
/// <param name="MaximumFilesPerMessage">How many files one message may carry, not counting thumbnails.</param>
/// <param name="MaximumThumbnailBytes">The largest thumbnail accepted.</param>
/// <param name="PreviewExtensions">Which types clients should show inline, when they can.</param>
/// <param name="PreviewAutoLoadMaximumBytes">The largest previewable file a client loads without being asked.</param>
/// <param name="UploadChunkBytes">The most bytes one <c>uploads/chunk</c> request may carry.</param>
public record AttachmentPolicyResponse(
    bool Enabled,
    long MaximumFileBytes,
    IReadOnlyDictionary<string, long> MaximumFileBytesByExtension,
    IReadOnlyList<string>? AllowedExtensions,
    IReadOnlyList<string> BlockedExtensions,
    bool AllowFilesWithoutExtension,
    int MaximumFilesPerMessage,
    long MaximumThumbnailBytes,
    IReadOnlyList<string> PreviewExtensions,
    long PreviewAutoLoadMaximumBytes,
    int UploadChunkBytes);

/// <summary>Where an upload stands.</summary>
/// <param name="AttachmentId">The upload.</param>
/// <param name="State"><c>uploading</c>, <c>uploaded</c> or <c>attached</c>.</param>
/// <param name="CiphertextSize">How many bytes it will be.</param>
/// <param name="ReceivedSize">How many have arrived: where the next chunk starts.</param>
/// <param name="ChunkSize">The most bytes one chunk may carry.</param>
/// <param name="ExpiresAtUtc">When it is thrown away unless it has been sent in a message.</param>
public record UploadStatus(
    Guid AttachmentId, string State, long CiphertextSize, long ReceivedSize, int ChunkSize, DateTime ExpiresAtUtc);

/// <param name="AttachmentId">The upload that was thrown away.</param>
public record UploadCancelled(Guid AttachmentId);
