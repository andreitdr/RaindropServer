using RaindropServer.Events;
using RaindropServer.Roles;

namespace RaindropServer.Channels.Voice;

/// <summary>
/// Re-checks everybody in a call whenever something that decides what they may do changes: a role
/// edited or deleted, a role given or taken away, a member leaving the server. Somebody who can no
/// longer see the channel or connect is removed from the call; somebody who gained or lost
/// <c>speak</c> has their microphone let through or stopped, and somebody who lost <c>video</c> or
/// <c>screen_share</c> has their camera or share ended.
/// </summary>
/// <remarks>
/// A single re-check of every participant rather than a precise one: working out whom a role change
/// touches would duplicate the resolver, and calls are small. Queued and coalesced through
/// <see cref="VoiceWorkQueue"/>, since handlers run inside the publish.
/// </remarks>
public sealed class VoicePermissionWatcher :
    IServerEventHandler<RoleSummary>,
    IServerEventHandler<RoleDeletedEvent>,
    IServerEventHandler<MemberRolesEvent>,
    IServerEventHandler<MemberEvent>
{
    private readonly VoiceService _voice;
    private readonly VoiceWorkQueue _work;

    public VoicePermissionWatcher(VoiceService voice, VoiceWorkQueue work)
    {
        _voice = voice;
        _work = work;
    }

    public Task HandleAsync(ServerEvent<RoleSummary> serverEvent, CancellationToken cancellationToken) => Recheck();

    public Task HandleAsync(ServerEvent<RoleDeletedEvent> serverEvent, CancellationToken cancellationToken) => Recheck();

    public Task HandleAsync(ServerEvent<MemberRolesEvent> serverEvent, CancellationToken cancellationToken) => Recheck();

    public Task HandleAsync(ServerEvent<MemberEvent> serverEvent, CancellationToken cancellationToken) =>
        serverEvent.Type == ServerEventTypes.MemberLeft ? Recheck() : Task.CompletedTask;

    private Task Recheck()
    {
        _work.EnqueueReevaluation(ct => _voice.ReevaluateAsync(null, ct));
        return Task.CompletedTask;
    }
}
