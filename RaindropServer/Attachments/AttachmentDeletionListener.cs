using RaindropServer.Channels;
using RaindropServer.Channels.Text;

namespace RaindropServer.Attachments;

/// <summary>
/// Lets go of a message's files when the message goes, and of a channel's when the channel does. It
/// only marks them deleted — which is quick, idempotent and safe to repeat, as a deletion listener must
/// be — and <see cref="AttachmentMaintenanceService"/> removes the files themselves afterwards.
/// </summary>
/// <remarks>
/// Unlike message text, attachments are never archived for the server owner: they are ciphertext the
/// server cannot read, and whose key went with the message.
/// </remarks>
public sealed class AttachmentDeletionListener : IMessageDeletionListener
{
    private readonly IAttachmentStore _store;

    public AttachmentDeletionListener(IAttachmentStore store) => _store = store;

    public Task OnMessageDeletingAsync(MessageDeletion deletion, CancellationToken cancellationToken) =>
        deletion.Message.IsEncrypted
            ? _store.MarkMessageDeletedAsync(deletion.Message.MessageId, deletion.DeletedAtUtc, cancellationToken)
            : Task.CompletedTask;

    public Task OnChannelMessagesDeletingAsync(ChannelDeletion deletion, CancellationToken cancellationToken) =>
        _store.MarkChannelDeletedAsync(deletion.Channel.ChannelId, deletion.DeletedAtUtc, cancellationToken);
}
