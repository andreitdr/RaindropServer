namespace RaindropServer.Channels.Text;

/// <summary>
/// The default <see cref="IMessageDeletionListener"/>: copies message text into the database before it
/// is destroyed, through <see cref="IDeletedMessageStore"/>.
/// </summary>
public sealed class ArchivingMessageDeletionListener : IMessageDeletionListener
{
    private readonly IDeletedMessageStore _archive;

    public ArchivingMessageDeletionListener(IDeletedMessageStore archive) => _archive = archive;

    /// <inheritdoc />
    public Task OnMessageDeletingAsync(MessageDeletion deletion, CancellationToken cancellationToken)
    {
        var message = deletion.Message;

        // A message that is already a tombstone has nothing left to keep. TextChannelService does not
        // call listeners for one, so this is only a guard for anything else that might.
        if (message.Content is null)
            return Task.CompletedTask;

        return _archive.ArchiveMessageAsync(new DeletedMessageRecord
        {
            MessageId = message.MessageId,
            ChannelId = message.ChannelId,
            ChannelName = deletion.Channel.Name,
            AuthorId = message.AuthorId,
            Content = message.Content,
            SentAtUtc = message.SentAtUtc,
            DeletedAtUtc = deletion.DeletedAtUtc,
            DeletedBy = deletion.DeletedBy,
            Reason = DeletedMessageReasons.MessageDeleted
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task OnChannelMessagesDeletingAsync(ChannelDeletion deletion, CancellationToken cancellationToken) =>
        _archive.ArchiveChannelMessagesAsync(deletion, cancellationToken);
}
