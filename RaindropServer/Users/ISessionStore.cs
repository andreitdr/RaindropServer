namespace RaindropServer.Users;

/// <summary>
/// Tracks logged-in clients. The default implementation keeps sessions in memory
/// (see <see cref="InMemorySessionStore"/>); a module could replace it to share sessions
/// across several instances of the same server.
/// </summary>
public interface ISessionStore
{
    /// <summary>Opens a new session and returns its token.</summary>
    UserSession Create(Guid userId, string username);

    /// <summary>
    /// Resolves a token to its session, extending the idle timeout. Returns false when the token is
    /// unknown or has expired.
    /// </summary>
    bool TryValidate(string token, out UserSession session);

    /// <summary>
    /// Whether a token still names a session that has not lapsed, <em>without</em> extending it.
    /// </summary>
    /// <remarks>
    /// The event stream asks this before every event it delivers, so that a session logged out or
    /// revoked elsewhere stops receiving anything at once. Sliding the timeout there would let a busy
    /// server keep an abandoned session alive just by talking to it.
    /// </remarks>
    bool IsActive(string token);

    /// <summary>Ends a single session — the logout path.</summary>
    void Remove(string token);

    /// <summary>
    /// Ends every session belonging to a user. Used when the credentials behind those sessions
    /// change or the user leaves the server.
    /// </summary>
    void RemoveAllForUser(Guid userId);

    /// <summary>
    /// Records that a session proved it runs on one of its user's devices. Returns false when the
    /// token no longer names a live session.
    /// </summary>
    /// <remarks>
    /// Default implementations keep a session store written before end-to-end encryption loading;
    /// such a store simply cannot bind sessions.
    /// </remarks>
    bool BindDevice(string token, Guid deviceId) =>
        throw new NotSupportedException($"{GetType().Name} does not support binding sessions to devices.");

    /// <summary>Ends every session bound to one device — when the device is revoked.</summary>
    void RemoveAllForDevice(Guid userId, Guid deviceId) =>
        throw new NotSupportedException($"{GetType().Name} does not support binding sessions to devices.");

    /// <summary>Keeps every live session for a user in step with a username change.</summary>
    void RenameUser(Guid userId, string username);

    /// <summary>
    /// Forgets every session that has lapsed, and says how many. A lapsed session is refused anyway;
    /// this is housekeeping, so a client that vanished without logging out does not hold memory until
    /// the next restart. <see cref="SessionSweepService"/> calls it on a timer.
    /// </summary>
    /// <remarks>
    /// The default forgets nothing, for a store that expires sessions by itself (or an older one).
    /// </remarks>
    int RemoveExpired() => 0;
}
