using RaindropServer.Events;

namespace RaindropServer.Members;

/// <summary>
/// The default <see cref="IPresenceTracker"/>: a member is online exactly when they have an event
/// stream open.
/// </summary>
/// <remarks>
/// A signed-in client keeps one stream open per server and heartbeats on it, and the stream is closed
/// when its session ends or when the heartbeats stop for <see cref="EventOptions.HeartbeatTimeout"/>.
/// So a client that was killed rather than closed reads as offline about a minute later, instead of
/// when its session finally lapses. A session alone — a client that logged in but never opened its
/// stream — does not count: there is nobody listening.
/// </remarks>
public sealed class StreamPresenceTracker : IPresenceTracker
{
    private readonly IEventConnectionRegistry _connections;

    public StreamPresenceTracker(IEventConnectionRegistry connections) => _connections = connections;

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<Guid, PresenceStatus>> GetAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        var online = _connections.ConnectedUserIds();

        IReadOnlyDictionary<Guid, PresenceStatus> statuses = userIds.ToDictionary(
            id => id,
            id => online.Contains(id) ? PresenceStatus.Online : PresenceStatus.Offline);

        return Task.FromResult(statuses);
    }
}
