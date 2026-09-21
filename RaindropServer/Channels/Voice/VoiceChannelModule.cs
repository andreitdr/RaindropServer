using RaindropServer.Events;
using RaindropServer.Roles;

namespace RaindropServer.Channels.Voice;

/// <summary>
/// Voice channels as far as <see cref="ChannelService"/> is concerned: that they exist, and what
/// deleting one means — ending its call. The call itself is <see cref="VoiceService"/>'s business.
/// </summary>
/// <remarks>
/// Registered whether or not a media server is configured. A server that had voice switched on and
/// then off still holds voice channels, and <see cref="ChannelService"/> refuses to delete a channel
/// whose type no module claims.
/// <para>
/// Deliberately depends on the stores and the transport rather than on <see cref="VoiceService"/>:
/// every module is a dependency of <see cref="ChannelService"/>, which <see cref="VoiceService"/>
/// needs, so the reverse would be a cycle.
/// </para>
/// </remarks>
public sealed class VoiceChannelModule : IChannelModule
{
    private readonly IVoiceStateStore _state;
    private readonly IVoiceTransport _transport;
    private readonly IEventPublisher _events;
    private readonly IPermissionResolver _permissions;

    public VoiceChannelModule(
        IVoiceStateStore state, IVoiceTransport transport, IEventPublisher events, IPermissionResolver permissions)
    {
        _state = state;
        _transport = transport;
        _events = events;
        _permissions = permissions;
    }

    public ChannelType Type => ChannelType.Voice;

    /// <summary>
    /// Ends the call. Unlike deleting a text channel's messages this is best-effort against the media
    /// server: an outage there must not stop a channel being deleted, and the reconciler closes a call
    /// whose channel is gone as soon as it can reach it.
    /// </summary>
    public async Task DeleteContentsAsync(ChannelDeletion deletion, CancellationToken cancellationToken)
    {
        var removed = await _state.RemoveChannelAsync(deletion.Channel.ChannelId, cancellationToken);

        foreach (var record in removed)
            await VoiceEvents.PublishLeftAsync(_events, _permissions, deletion.Channel, record, VoiceLeaveReasons.ChannelDeleted);

        try
        {
            await _transport.CloseCallAsync(deletion.Channel.ChannelId, cancellationToken);
        }
        catch (VoiceTransportException ex)
        {
            Console.Error.WriteLine($"[raindrop] voice: could not end the call in '{deletion.Channel.Name}': {ex.Message}");
        }
    }
}
