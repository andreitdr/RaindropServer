using RaindropServer.Events;
using RaindropServer.Users;

namespace RaindropServer.Members;

/// <summary>
/// Announces <see cref="ServerEventTypes.MemberPresence"/> whenever a member's sessions change, so a
/// roster stays current without being re-read.
/// </summary>
/// <remarks>
/// It asks the <see cref="IPresenceTracker"/> rather than reading sessions itself, so a server that
/// swaps in a different idea of presence gets events that agree with its roster. It announces the
/// member's presence after every change, even when that is the same as before — a second device
/// logging in re-announces "online" — because the event carries the state rather than a transition,
/// and repeating a state is harmless where missing one is not.
/// <para>
/// What it cannot see is a session lapsing on its own: that happens lazily, inside the session store,
/// with nobody to tell. An open event stream keeps its session renewed, so the members this affects
/// are ones with no stream open — who read as online until the idle timeout, exactly as the roster
/// has them.
/// </para>
/// </remarks>
public sealed class PresenceBroadcaster : ISessionListener
{
    private readonly IPresenceTracker _presence;
    private readonly IEventPublisher _events;

    public PresenceBroadcaster(IPresenceTracker presence, IEventPublisher events)
    {
        _presence = presence;
        _events = events;
    }

    /// <inheritdoc />
    public async Task OnSessionsChangedAsync(Guid userId, CancellationToken cancellationToken)
    {
        var statuses = await _presence.GetAsync([userId], cancellationToken);
        var status = statuses.TryGetValue(userId, out var found) ? found : PresenceStatus.Offline;

        await _events.PublishAsync(ServerEventTypes.MemberPresence,
            new MemberPresenceEvent(userId, PresenceStatusNames.Of(status)));
    }
}
