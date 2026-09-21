using RaindropServer.Users;

namespace RaindropServer.Members;

/// <summary>
/// The default <see cref="IPresenceTracker"/>: a member is online exactly when they hold a session
/// that has not lapsed.
/// </summary>
/// <remarks>
/// This is as much as the server can honestly know. Clients are expected to log out when they close,
/// and the idle timeout (12 hours by default, see <see cref="UserOptions.SessionIdleTimeout"/>) is
/// the backstop for the ones that simply vanish — so a client killed rather than closed reads as
/// online until its session lapses. Shortening that timeout is what makes presence sharper; a
/// heartbeat the client sends would make it sharper still, and would live behind this same interface.
/// </remarks>
public sealed class SessionPresenceTracker : IPresenceTracker
{
    private readonly ISessionStore _sessions;

    public SessionPresenceTracker(ISessionStore sessions) => _sessions = sessions;

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<Guid, PresenceStatus>> GetAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        var online = _sessions.ActiveUserIds();

        IReadOnlyDictionary<Guid, PresenceStatus> statuses = userIds.ToDictionary(
            id => id,
            id => online.Contains(id) ? PresenceStatus.Online : PresenceStatus.Offline);

        return Task.FromResult(statuses);
    }
}
