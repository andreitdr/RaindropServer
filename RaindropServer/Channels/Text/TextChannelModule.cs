namespace RaindropServer.Channels.Text;

/// <summary>
/// Registers text channels as a kind of channel this server can host. Its presence in the container
/// is what lets <see cref="ChannelService"/> create one; the work of a text channel is
/// <see cref="TextChannelService"/>'s.
/// </summary>
public sealed class TextChannelModule : IChannelModule
{
    private readonly ITextMessageStore _messages;
    private readonly IReadOnlyList<IMessageDeletionListener> _listeners;

    public TextChannelModule(ITextMessageStore messages, IEnumerable<IMessageDeletionListener> listeners)
    {
        _messages = messages;
        _listeners = listeners.ToList();
    }

    /// <inheritdoc />
    public ChannelType Type => ChannelType.Text;

    /// <summary>
    /// Tells every <see cref="IMessageDeletionListener"/> first — which is when the default one copies
    /// the messages into the database — and only then deletes them. A listener that throws stops the
    /// deletion before anything has been removed.
    /// </summary>
    public async Task DeleteContentsAsync(ChannelDeletion deletion, CancellationToken cancellationToken)
    {
        foreach (var listener in _listeners)
            await listener.OnChannelMessagesDeletingAsync(deletion, cancellationToken);

        await _messages.DeleteChannelMessagesAsync(deletion.Channel.ChannelId, cancellationToken);
    }
}
