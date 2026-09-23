using System.Security.Cryptography;
using RaindropServer.Channels;
using RaindropServer.Channels.Text;
using RaindropServer.Roles;
using RaindropServer.Users;

namespace RaindropServer.Attachments;

/// <summary>
/// Every rule about files added to messages: starting an upload, receiving it in chunks, finishing it,
/// tying it to a message, and handing it back to the members who may read it.
/// </summary>
/// <remarks>
/// Files are end-to-end encrypted by the sender before a byte reaches the server, with a key of their
/// own that travels inside the message. So this service stores ciphertext and judges only what an
/// upload declares in the clear: which channel it is for, its extension, and a ciphertext length that
/// fixes the plaintext length exactly (<see cref="AttachmentFormat"/>). Who may read a file is who may
/// read its channel — the same answer <see cref="ChannelService.ResolveAsync"/> gives for messages.
/// <para>
/// An upload is written sequentially, one chunk after another at the offset the server says it has
/// reached; a chunk that fails part-way is sent again from that offset. Chunks of one upload are
/// serialised in-process, the same single-process assumption <see cref="InMemorySessionStore"/> makes.
/// </para>
/// </remarks>
public sealed class AttachmentService : IMessageAttachments
{
    private readonly ChannelService _channels;
    private readonly IAttachmentStore _store;
    private readonly IAttachmentStorage _storage;
    private readonly IReadOnlyList<IAttachmentPolicy> _policies;
    private readonly IReadOnlyList<IAttachmentListener> _listeners;
    private readonly AttachmentOptions _options;
    private readonly TimeProvider _time;

    // Striped rather than one per upload, so nothing has to be cleaned up when an upload finishes.
    private readonly SemaphoreSlim[] _uploadLocks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public AttachmentService(
        ChannelService channels,
        IAttachmentStore store,
        IAttachmentStorage storage,
        IEnumerable<IAttachmentPolicy> policies,
        IEnumerable<IAttachmentListener> listeners,
        AttachmentOptions options,
        TimeProvider? time = null)
    {
        _channels = channels;
        _store = store;
        _storage = storage;
        _policies = policies.ToList();
        _listeners = listeners.ToList();
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    /// <summary>What this server accepts, for a client to check a file against before uploading it.</summary>
    public AttachmentPolicyResponse GetPolicy() => new(
        _options.Enabled,
        _options.MaximumFileBytes,
        new Dictionary<string, long>(_options.MaximumFileBytesByExtension),
        _options.AllowedExtensions,
        _options.BlockedExtensions,
        _options.AllowFilesWithoutExtension,
        _options.MaximumFilesPerMessage,
        _options.MaximumThumbnailBytes,
        _options.PreviewExtensions.Where(_options.IsTypeAllowed).ToList(),
        _options.PreviewAutoLoadMaximumBytes,
        _options.UploadChunkBytes);

    /// <summary>
    /// Starts an upload: checks that the caller may post files in the channel and that the declared
    /// file is one this server accepts, and reserves an id for it.
    /// </summary>
    public async Task<UploadStatus> CreateUploadAsync(
        UserSession session, CreateUploadRequest request, CancellationToken cancellationToken)
    {
        RequireEnabled();
        var deviceId = RequireDevice(session);

        var access = await _channels.ResolveAsync(session.UserId, request.ChannelId, ChannelType.Text, cancellationToken);
        access.Permissions.Require(Permission.SendMessages, "post in this channel");
        access.Permissions.Require(Permission.AttachFiles, "add files to messages here");

        var kind = string.IsNullOrWhiteSpace(request.Kind) ? AttachmentNames.FileKind : request.Kind.Trim().ToLowerInvariant();
        if (!AttachmentNames.Kinds.Contains(kind))
            throw ApiException.BadRequest("invalid_attachment",
                $"'{request.Kind}' is not a kind of attachment; use {string.Join(" or ", AttachmentNames.Kinds)}.");

        var extension = (request.Extension ?? "").Trim().TrimStart('.').ToLowerInvariant();
        if (!AttachmentOptions.IsWellFormedExtension(extension))
            throw ApiException.BadRequest("invalid_attachment",
                $"An extension is 1 to {AttachmentOptions.MaximumExtensionLength} letters and digits.");

        var plaintext = AttachmentFormat.PlaintextLength(request.CiphertextSize)
                        ?? throw ApiException.BadRequest("invalid_attachment",
                            $"No encrypted file is {request.CiphertextSize} bytes long; encrypt it with raindrop/attachment/v1.");

        var check = new AttachmentUploadCheck(session, access.Channel, kind, extension, plaintext);
        foreach (var policy in _policies)
            await policy.CheckUploadAsync(check, cancellationToken);

        var attachment = new AttachmentRecord
        {
            AttachmentId = Guid.NewGuid(),
            ChannelId = access.Channel.ChannelId,
            UploaderId = session.UserId,
            UploaderDeviceId = deviceId,
            Kind = kind,
            Extension = extension,
            CiphertextSize = request.CiphertextSize,
            State = AttachmentState.Uploading,
            CreatedAtUtc = UtcNow
        };

        await _store.InsertAsync(attachment, cancellationToken);
        return StatusOf(attachment);
    }

    /// <summary>
    /// Appends one chunk of an upload, read from <paramref name="body"/>. <paramref name="offset"/> must
    /// be exactly where the upload has reached; a chunk that would run past the declared size, or past
    /// <see cref="AttachmentOptions.UploadChunkBytes"/>, is refused and nothing it carried is kept.
    /// </summary>
    public async Task<UploadStatus> WriteChunkAsync(
        UserSession session, Guid attachmentId, long offset, Stream body, CancellationToken cancellationToken)
    {
        RequireEnabled();
        RequireDevice(session);

        var gate = _uploadLocks[(attachmentId.GetHashCode() & int.MaxValue) % _uploadLocks.Length];
        await gate.WaitAsync(cancellationToken);
        try
        {
            var attachment = await FindOwnAsync(session, attachmentId, cancellationToken);
            RequireUploading(attachment);

            if (offset != attachment.ReceivedSize)
                throw ApiException.Conflict("upload_offset_mismatch",
                    $"This upload has received {attachment.ReceivedSize} bytes; send the next chunk from there.");

            var room = Math.Min(_options.UploadChunkBytes, attachment.CiphertextSize - offset);
            long written;

            await using (var target = await _storage.OpenAppendAsync(attachment.StorageKey, offset, cancellationToken))
                written = await CopyAtMostAsync(body, target, room, cancellationToken);

            if (written == 0)
                throw ApiException.BadRequest("invalid_payload", "A chunk cannot be empty.");

            var received = offset + written;
            if (!await _store.UpdateReceivedAsync(attachmentId, offset, received, cancellationToken))
                throw ApiException.Conflict("upload_offset_mismatch", "This upload changed while the chunk arrived; ask for its status.");

            return StatusOf(attachment with { ReceivedSize = received });
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Where one of the caller's uploads stands, so an interrupted one can carry on.</summary>
    public async Task<UploadStatus> GetStatusAsync(UserSession session, Guid attachmentId, CancellationToken cancellationToken)
    {
        var attachment = await FindOwnAsync(session, attachmentId, cancellationToken);

        if (attachment.State == AttachmentState.Deleted)
            throw NotFound(attachmentId);

        return StatusOf(attachment);
    }

    /// <summary>
    /// Finishes an upload once every byte has arrived: the stored bytes must hash to what the uploader
    /// says they encrypted. A mismatch throws the upload away, since some of what was stored is not
    /// what was sent.
    /// </summary>
    public async Task<UploadStatus> CompleteAsync(
        UserSession session, CompleteUploadRequest request, CancellationToken cancellationToken)
    {
        RequireEnabled();
        RequireDevice(session);

        if (request.CiphertextSha256 is not { Length: 32 })
            throw ApiException.BadRequest("invalid_payload", "ciphertextSha256 must be the 32-byte SHA-256 of the encrypted file.");

        var gate = _uploadLocks[(request.AttachmentId.GetHashCode() & int.MaxValue) % _uploadLocks.Length];
        await gate.WaitAsync(cancellationToken);
        try
        {
            var attachment = await FindOwnAsync(session, request.AttachmentId, cancellationToken);

            // Completing twice is not an error: the caller wanted it finished, and it is.
            if (attachment.State is AttachmentState.Uploaded or AttachmentState.Attached)
                return StatusOf(attachment);

            RequireUploading(attachment);

            if (attachment.ReceivedSize != attachment.CiphertextSize)
                throw ApiException.Conflict("upload_incomplete",
                    $"This upload has {attachment.ReceivedSize} of its {attachment.CiphertextSize} bytes.");

            byte[] hash;
            await using (var stored = await _storage.OpenPendingReadAsync(attachment.StorageKey, cancellationToken))
                hash = await SHA256.HashDataAsync(stored, cancellationToken);

            if (!CryptographicOperations.FixedTimeEquals(hash, request.CiphertextSha256))
            {
                await DiscardAsync(attachment, cancellationToken);
                throw ApiException.Conflict("hash_mismatch",
                    "The file that arrived is not the one that was encrypted; upload it again from the start.");
            }

            await _storage.CommitAsync(attachment.StorageKey, cancellationToken);

            var completedAt = UtcNow;
            if (!await _store.CompleteAsync(attachment.AttachmentId, hash, completedAt, cancellationToken))
                throw ApiException.Conflict("upload_offset_mismatch", "This upload changed while it was being finished.");

            var uploaded = attachment with
            {
                State = AttachmentState.Uploaded,
                CiphertextSha256 = hash,
                CompletedAtUtc = completedAt
            };

            foreach (var listener in _listeners)
                await Notify(() => listener.OnUploadedAsync(uploaded, cancellationToken));

            return StatusOf(uploaded);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Throws away one of the caller's uploads that has not been sent in a message.</summary>
    public async Task CancelAsync(UserSession session, Guid attachmentId, CancellationToken cancellationToken)
    {
        var attachment = await FindOwnAsync(session, attachmentId, cancellationToken);

        if (attachment.State == AttachmentState.Attached)
            throw ApiException.Conflict("attachment_already_sent",
                "That file has been sent in a message; delete the message to remove it.");

        if (attachment.State != AttachmentState.Deleted)
            await DiscardAsync(attachment, cancellationToken);
    }

    /// <summary>
    /// Opens an attachment's ciphertext for somebody who may read it: anybody who can see the channel
    /// of the message it was sent in, or its uploader before it has been sent. Anything else — an id
    /// that does not exist, a channel the caller cannot see, somebody else's unsent upload — is the same
    /// <c>attachment_not_found</c>, so ids cannot be probed.
    /// </summary>
    public async Task<(AttachmentRecord Attachment, Stream Content)> OpenDownloadAsync(
        UserSession session, Guid attachmentId, CancellationToken cancellationToken)
    {
        var attachment = attachmentId == Guid.Empty ? null : await _store.FindAsync(attachmentId, cancellationToken);

        switch (attachment?.State)
        {
            case AttachmentState.Attached:
                try
                {
                    await _channels.ResolveAsync(session.UserId, attachment.ChannelId, ChannelType.Text, cancellationToken);
                }
                catch (ApiException)
                {
                    throw NotFound(attachmentId);
                }

                break;

            case AttachmentState.Uploaded when attachment.UploaderId == session.UserId:
                break;

            default:
                throw NotFound(attachmentId);
        }

        try
        {
            return (attachment, await _storage.OpenReadAsync(attachment.StorageKey, cancellationToken));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw ApiException.NotFound("attachment_unavailable",
                "That file is missing from this server's storage. Tell the server owner.");
        }
    }

    // ── IMessageAttachments ──────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<MessageAttachment>> AttachAsync(
        UserSession session, ChannelAccess access, Guid messageId, IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken)
    {
        RequireEnabled();
        access.Permissions.Require(Permission.AttachFiles, "add files to messages here");

        if (attachmentIds.Distinct().Count() != attachmentIds.Count)
            throw ApiException.BadRequest("invalid_attachment", "A message cannot carry the same file twice.");

        if (attachmentIds.Count > _options.MaximumFilesPerMessage * 2)
            throw TooMany();

        var attachments = new List<AttachmentRecord>(attachmentIds.Count);
        foreach (var id in attachmentIds)
        {
            var attachment = await _store.FindAsync(id, cancellationToken);

            if (attachment is null || attachment.UploaderId != session.UserId || attachment.State == AttachmentState.Deleted)
                throw NotFound(id);

            if (attachment.ChannelId != access.Channel.ChannelId)
                throw ApiException.BadRequest("invalid_attachment", $"File {id} was uploaded for a different channel.");

            switch (attachment.State)
            {
                case AttachmentState.Uploading:
                    throw ApiException.Conflict("upload_incomplete", $"File {id} has not finished uploading.");
                case AttachmentState.Attached:
                    throw ApiException.Conflict("attachment_already_sent", $"File {id} has already been sent in a message.");
            }

            attachments.Add(attachment);
        }

        var files = attachments.Count(attachment => attachment.Kind == AttachmentNames.FileKind);
        if (files > _options.MaximumFilesPerMessage || attachments.Count - files > files)
            throw TooMany();

        if (!await _store.ClaimAsync(attachmentIds, messageId, session.UserId, access.Channel.ChannelId, cancellationToken))
            throw ApiException.Conflict("attachment_already_sent", "One of these files was sent in another message just now.");

        return attachments.Select(ToContract).ToList();
    }

    /// <inheritdoc />
    public Task DetachAsync(Guid messageId, CancellationToken cancellationToken) =>
        _store.ReleaseAsync(messageId, cancellationToken);

    /// <inheritdoc />
    public async Task OnMessageStoredAsync(
        Guid messageId, IReadOnlyList<MessageAttachment> attachments, CancellationToken cancellationToken)
    {
        var ids = attachments.Select(attachment => attachment.AttachmentId).ToList();

        foreach (var listener in _listeners)
            await Notify(() => listener.OnAttachedAsync(messageId, ids, cancellationToken));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<MessageAttachment>>> ListAsync(
        IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken)
    {
        var attachments = await _store.ListForMessagesAsync(messageIds, cancellationToken);

        return attachments
            .GroupBy(attachment => attachment.MessageId!.Value)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<MessageAttachment>)group.Select(ToContract).ToList());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void RequireEnabled()
    {
        if (!_options.Enabled)
            throw ApiException.BadRequest("attachments_disabled", "This server does not accept files.");
    }

    private static Guid RequireDevice(UserSession session) =>
        session.DeviceId ?? throw ApiException.Forbidden("device_required",
            "Files are end-to-end encrypted, so they are uploaded from a session bound to one of your devices.");

    private static void RequireUploading(AttachmentRecord attachment)
    {
        if (attachment.State != AttachmentState.Uploading)
            throw ApiException.Conflict("upload_finished", "This upload has already been finished or thrown away.");
    }

    private async Task<AttachmentRecord> FindOwnAsync(UserSession session, Guid attachmentId, CancellationToken cancellationToken)
    {
        var attachment = attachmentId == Guid.Empty ? null : await _store.FindAsync(attachmentId, cancellationToken);

        return attachment is not null && attachment.UploaderId == session.UserId
            ? attachment
            : throw NotFound(attachmentId);
    }

    private async Task DiscardAsync(AttachmentRecord attachment, CancellationToken cancellationToken)
    {
        await _store.MarkDeletedAsync(attachment.AttachmentId, UtcNow, cancellationToken);

        // Best effort: the maintenance pass removes the file of every deleted record anyway.
        try
        {
            await _storage.DeleteAsync(attachment.StorageKey, cancellationToken);
        }
        catch (IOException)
        {
        }
    }

    private UploadStatus StatusOf(AttachmentRecord attachment) => new(
        attachment.AttachmentId,
        AttachmentNames.Of(attachment.State),
        attachment.CiphertextSize,
        attachment.ReceivedSize,
        _options.UploadChunkBytes,
        attachment.CreatedAtUtc + _options.PendingUploadExpiry);

    private static MessageAttachment ToContract(AttachmentRecord attachment) =>
        new(attachment.AttachmentId, attachment.Kind, attachment.Extension, attachment.CiphertextSize,
            attachment.CiphertextSha256);

    private ApiException TooMany() => ApiException.BadRequest("too_many_attachments",
        $"A message may carry at most {_options.MaximumFilesPerMessage} files, and a thumbnail for each.");

    private static ApiException NotFound(Guid attachmentId) =>
        ApiException.NotFound("attachment_not_found", $"There is no file {attachmentId} you can reach on this server.");

    /// <summary>
    /// Copies up to <paramref name="limit"/> bytes. More than that is refused — the chunk is too big,
    /// or runs past the end of the file — and what was written is left for the next chunk to replace.
    /// </summary>
    private static async Task<long> CopyAtMostAsync(Stream source, Stream target, long limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;

            total += read;
            if (total > limit)
                throw new ApiException(StatusCodes.PayloadTooLarge, "chunk_too_large",
                    $"A chunk may carry at most {limit} more bytes of this upload.");

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        await target.FlushAsync(cancellationToken);
        return total;
    }

    private static async Task Notify(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[raindrop] an attachment listener failed: {ex.Message}");
        }
    }

    private static class StatusCodes
    {
        public const int PayloadTooLarge = 413;
    }
}
