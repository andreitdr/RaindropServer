using RaindropServer.Events;
using RaindropServer.Roles;

namespace RaindropServer.Channels.Voice;

/// <summary>
/// Announcing voice state. Shared by <see cref="VoiceService"/> and <see cref="VoiceChannelModule"/>,
/// which cannot reach each other — the module is a dependency of <see cref="ChannelService"/>, which
/// the service depends on in turn.
/// </summary>
public static class VoiceEvents
{
    /// <summary>
    /// Everybody who may see <paramref name="channel"/>, plus the member the event is about — who has
    /// to hear that they were removed even when losing sight of the channel is the reason why. A
    /// channel that no longer exists narrows it to that member alone; everybody else has already been
    /// told the channel is gone.
    /// </summary>
    public static EventAudience Audience(IPermissionResolver permissions, ChannelRecord? channel, Guid userId)
    {
        if (channel is null)
            return EventAudiences.Only(userId);

        var viewers = ChannelAudience.For(permissions, channel);
        return (recipient, cancellationToken) =>
            recipient == userId ? ValueTask.FromResult(true) : viewers(recipient, cancellationToken);
    }

    /// <summary>Turns a stored record into the shape clients see.</summary>
    public static VoiceParticipant ToContract(VoiceParticipantRecord record) => new(
        record.ChannelId,
        record.UserId,
        record.SelfMute,
        record.SelfDeaf,
        record.ServerMute,
        record.Suppressed,
        record.Connected,
        record.JoinedAtUtc);

    /// <summary>Publishes <c>voice.state</c> for one participant.</summary>
    public static Task PublishStateAsync(
        IEventPublisher events, IPermissionResolver permissions, ChannelRecord? channel, VoiceParticipantRecord record) =>
        events.PublishAsync(ServerEventTypes.VoiceState, ToContract(record),
            Audience(permissions, channel, record.UserId));

    /// <summary>Publishes <c>voice.left</c> for one participant.</summary>
    public static Task PublishLeftAsync(
        IEventPublisher events, IPermissionResolver permissions, ChannelRecord? channel,
        VoiceParticipantRecord record, string reason) =>
        events.PublishAsync(ServerEventTypes.VoiceLeft, new VoiceLeftEvent(record.ChannelId, record.UserId, reason),
            Audience(permissions, channel, record.UserId));
}
