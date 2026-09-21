using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace RaindropServer.Users;

/// <summary>
/// The default <see cref="ISessionStore"/>: a concurrent dictionary of tokens, swept lazily.
/// Sessions are intentionally lost on restart — see <see cref="UserSession"/>.
/// </summary>
public sealed class InMemorySessionStore : ISessionStore
{
    private const int TokenBytes = 32;

    private readonly ConcurrentDictionary<string, UserSession> _sessions = new(StringComparer.Ordinal);
    private readonly UserOptions _options;
    private readonly TimeProvider _time;

    public InMemorySessionStore(UserOptions options, TimeProvider? time = null)
    {
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    public UserSession Create(Guid userId, string username)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var session = new UserSession(
            Token: Base64Url(RandomNumberGenerator.GetBytes(TokenBytes)),
            UserId: userId,
            Username: username,
            CreatedAtUtc: now,
            ExpiresAtUtc: now + _options.SessionIdleTimeout);

        _sessions[session.Token] = session;
        return session;
    }

    public bool TryValidate(string token, out UserSession session)
    {
        session = null!;

        if (string.IsNullOrEmpty(token) || !_sessions.TryGetValue(token, out var found))
            return false;

        var now = _time.GetUtcNow().UtcDateTime;

        if (found.ExpiresAtUtc <= now)
        {
            _sessions.TryRemove(token, out _);
            return false;
        }

        // Slide the idle timeout forward on every authenticated request.
        session = found with { ExpiresAtUtc = now + _options.SessionIdleTimeout };
        _sessions[token] = session;
        return true;
    }

    public bool IsActive(string token) =>
        !string.IsNullOrEmpty(token)
        && _sessions.TryGetValue(token, out var session)
        && session.ExpiresAtUtc > _time.GetUtcNow().UtcDateTime;

    public void Remove(string token) => _sessions.TryRemove(token, out _);

    public void RemoveAllForUser(Guid userId)
    {
        foreach (var (token, session) in _sessions)
        {
            if (session.UserId == userId)
                _sessions.TryRemove(token, out _);
        }
    }

    public void RenameUser(Guid userId, string username)
    {
        foreach (var (token, session) in _sessions)
        {
            if (session.UserId == userId)
                _sessions.TryUpdate(token, session with { Username = username }, session);
        }
    }

    public IReadOnlySet<Guid> ActiveUserIds()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var online = new HashSet<Guid>();

        foreach (var (token, session) in _sessions)
        {
            // Iterating anyway, so this is where lapsed sessions get swept: leaving them in would
            // report somebody as online for as long as nobody happened to use their token again.
            if (session.ExpiresAtUtc <= now)
                _sessions.TryRemove(token, out _);
            else
                online.Add(session.UserId);
        }

        return online;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
