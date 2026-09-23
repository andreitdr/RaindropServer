using RaindropServer.Channels;
using RaindropServer.Users;

namespace RaindropServer.Attachments;

/// <summary>
/// Decides whether an upload may start. Every registered policy is asked, and each can refuse by
/// throwing an <see cref="ApiException"/>; <see cref="ConfiguredAttachmentPolicy"/> applies the owner's
/// <c>raindrop.json</c> rules, and a plugin adds its own beside it with <c>TryAddEnumerable</c> — a quota
/// per member, say, or a rule for one channel.
/// </summary>
/// <remarks>
/// A policy sees only what the server can: the file is end-to-end encrypted, so there is no looking
/// inside it. Permissions have already been checked when a policy runs.
/// </remarks>
public interface IAttachmentPolicy
{
    Task CheckUploadAsync(AttachmentUploadCheck upload, CancellationToken cancellationToken);
}

/// <param name="Session">Who is uploading.</param>
/// <param name="Channel">The text channel it is for.</param>
/// <param name="Kind">A file, or a thumbnail of one.</param>
/// <param name="Extension">Lower case, without the dot; empty for none.</param>
/// <param name="PlaintextSize">The file's size, derived exactly from the declared ciphertext size.</param>
public sealed record AttachmentUploadCheck(
    UserSession Session, ChannelRecord Channel, string Kind, string Extension, long PlaintextSize);

/// <summary>The owner's rules from <c>raindrop.json</c>: types, sizes, and how many uploads may wait.</summary>
public sealed class ConfiguredAttachmentPolicy : IAttachmentPolicy
{
    private readonly AttachmentOptions _options;
    private readonly IAttachmentStore _store;

    public ConfiguredAttachmentPolicy(AttachmentOptions options, IAttachmentStore store)
    {
        _options = options;
        _store = store;
    }

    public async Task CheckUploadAsync(AttachmentUploadCheck upload, CancellationToken cancellationToken)
    {
        if (upload.Kind == AttachmentNames.ThumbnailKind)
        {
            // A preview the sender made for another file in the message; its type is whatever image
            // format the client chose, so only its size is the owner's business.
            if (upload.PlaintextSize > _options.MaximumThumbnailBytes)
                throw ApiException.BadRequest("file_too_large",
                    $"A thumbnail may be at most {_options.MaximumThumbnailBytes / 1024} KB.");
        }
        else
        {
            if (!_options.IsTypeAllowed(upload.Extension))
                throw ApiException.BadRequest("file_type_not_allowed", upload.Extension.Length == 0
                    ? "This server does not accept files without an extension."
                    : $"This server does not accept .{upload.Extension} files.");

            var limit = _options.MaximumBytesFor(upload.Extension);
            if (upload.PlaintextSize > limit)
                throw ApiException.BadRequest("file_too_large",
                    $"Files{(upload.Extension.Length == 0 ? "" : $" of type .{upload.Extension}")} may be at most " +
                    $"{AttachmentOptions.Describe(limit)} on this server.");
        }

        if (await _store.CountPendingAsync(upload.Session.UserId, cancellationToken) >= _options.MaximumPendingUploadsPerMember)
            throw ApiException.Conflict("too_many_pending_uploads",
                $"You have {_options.MaximumPendingUploadsPerMember} uploads waiting to be sent. Send or cancel some first.");
    }
}
