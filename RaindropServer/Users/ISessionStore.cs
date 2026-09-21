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

    /// <summary>Keeps every live session for a user in step with a username change.</summary>
    void RenameUser(Guid userId, string username);

    /// <summary>
    /// The UUIDs holding at least one session that has not lapsed — which is to say, who is logged
    /// in right now.
    /// </summary>
    /// <remarks>
    /// The whole set in one call rather than a per-user question, because the only caller is a
    /// roster asking about every member at once (see <c>Members/SessionPresenceTracker</c>). The
    /// presence <em>meaning</em> of the answer is deliberately not this interface's business: it
    /// reports sessions, and something else decides what a session implies.
    /// </remarks>
    IReadOnlySet<Guid> ActiveUserIds();
}
