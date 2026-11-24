using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RaindropServer.Users;

namespace RaindropServer.Dashboard;

/// <summary>The dashboard's one account, as kept in <see cref="DashboardOptions.CredentialsPath"/>.</summary>
public sealed record DashboardAccount(string Username, string PasswordHash, DateTime CreatedAtUtc, DateTime? PasswordChangedAtUtc = null);

/// <summary>A signed-in browser.</summary>
public sealed record DashboardSession(string Token, string Username, DateTime CreatedAtUtc, DateTime ExpiresAtUtc);

/// <summary>
/// Who may use the dashboard. It has exactly one account, which is not a Raindrop account: most owners'
/// Raindrop accounts have no password at all now that clients sign in with a device key, and the
/// dashboard has to keep working on a server whose owner lost every device.
/// </summary>
/// <remarks>
/// <para>
/// <b>First setup.</b> With no account on disk, a one-time setup code is made at start and printed to
/// the console (and so to <c>docker logs</c>). The first visit asks for it with a new username and
/// password; whoever cannot read the server's output cannot claim the dashboard, however they reached
/// its port. The code lives in memory: a restart makes a new one, and using it ends it.
/// </para>
/// <para>
/// <b>Sessions</b> are in memory, as Raindrop's own are, so a restart signs the browser out. Each is a
/// random 256-bit token in a cookie, ending after <see cref="DashboardOptions.SessionIdleTimeout"/> idle
/// or <see cref="DashboardOptions.SessionLifetime"/> in all.
/// </para>
/// </remarks>
public sealed class DashboardAuth
{
    private const int MaximumSessions = 32;
    private const int MaximumUsernameLength = 64;
    private const int MaximumPasswordLength = 256;
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private static readonly JsonSerializerOptions FileFormat = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly DashboardOptions _options;
    private readonly IPasswordHasher _hasher;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DashboardSession> _sessions = new(StringComparer.Ordinal);
    private DashboardAccount? _account;
    private string? _setupCode;
    private bool _loaded;

    public DashboardAuth(DashboardOptions options, IPasswordHasher hasher, TimeProvider? time = null)
    {
        _options = options;
        _hasher = hasher;
        _time = time ?? TimeProvider.System;
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    /// <summary>Whether the dashboard has its account yet.</summary>
    public bool IsSetUp
    {
        get
        {
            lock (_gate)
            {
                EnsureLoaded();
                return _account is not null;
            }
        }
    }

    /// <summary>The code the first visit needs, while there is no account; null once there is one.</summary>
    public string? PendingSetupCode
    {
        get
        {
            lock (_gate)
            {
                EnsureLoaded();
                return _setupCode;
            }
        }
    }

    /// <summary>
    /// Reads the account file, throwing when it is there but cannot be used — so a startup check can
    /// say so before anything listens, rather than every sign-in failing.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file cannot be read or is not an account.</exception>
    public void Load()
    {
        lock (_gate)
        {
            _loaded = false;
            EnsureLoaded();
        }
    }

    /// <summary>Creates the account with the setup code, and signs the browser in.</summary>
    public DashboardSession SetUp(string? code, string? username, string? password)
    {
        lock (_gate)
        {
            EnsureLoaded();

            if (_account is not null || _setupCode is null)
                throw ApiException.Conflict("already_set_up", "The dashboard already has its account. Sign in instead.");

            if (!CodeMatches(code, _setupCode))
                throw ApiException.Unauthorized("invalid_setup_code",
                    "That is not the setup code. It is printed in the server's console and log when the server starts.");

            var name = CheckUsername(username);
            CheckNewPassword(password);

            var account = new DashboardAccount(name, _hasher.Hash(password!), UtcNow);
            Save(account);

            _account = account;
            _setupCode = null;
            _options.Audit($"set up, signed in as '{name}'");
            return OpenSession(name);
        }
    }

    /// <summary>Signs a browser in, or refuses with one answer for a wrong name and a wrong password alike.</summary>
    public DashboardSession SignIn(string? username, string? password)
    {
        DashboardAccount? account;
        lock (_gate)
        {
            EnsureLoaded();
            account = _account;
        }

        if (account is null)
            throw ApiException.Conflict("not_set_up", "The dashboard has no account yet. Set it up with the code the server printed.");

        // Hashing happens outside the lock — it is the slow part, on purpose — and always, so a wrong
        // username takes as long to refuse as a wrong password.
        var passwordMatches = _hasher.Verify(password ?? "", account.PasswordHash);
        var nameMatches = string.Equals((username ?? "").Trim(), account.Username, StringComparison.OrdinalIgnoreCase);

        if (!passwordMatches || !nameMatches)
            throw ApiException.Unauthorized("invalid_credentials", "Wrong username or password.");

        lock (_gate)
        {
            _options.Audit($"signed in as '{account.Username}'");
            return OpenSession(account.Username);
        }
    }

    /// <summary>The session behind <paramref name="token"/>, extending its idle time; false when there is none.</summary>
    public bool TryValidate(string? token, out DashboardSession session)
    {
        session = null!;
        if (string.IsNullOrEmpty(token))
            return false;

        lock (_gate)
        {
            if (!_sessions.TryGetValue(token, out var found))
                return false;

            var now = UtcNow;
            var ends = found.CreatedAtUtc + _options.SessionLifetime;

            if (found.ExpiresAtUtc <= now || ends <= now)
            {
                _sessions.Remove(token);
                return false;
            }

            session = found with { ExpiresAtUtc = Min(now + _options.SessionIdleTimeout, ends) };
            _sessions[token] = session;
            return true;
        }
    }

    /// <summary>Signs one browser out.</summary>
    public void SignOut(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return;

        lock (_gate)
            _sessions.Remove(token);
    }

    /// <summary>Changes the password, signing out every other browser.</summary>
    public void ChangePassword(DashboardSession session, string? currentPassword, string? newPassword)
    {
        DashboardAccount account;
        lock (_gate)
        {
            EnsureLoaded();
            account = _account ?? throw ApiException.Conflict("not_set_up", "The dashboard has no account.");
        }

        if (!_hasher.Verify(currentPassword ?? "", account.PasswordHash))
            throw ApiException.Unauthorized("invalid_credentials", "The current password is wrong.");

        CheckNewPassword(newPassword);
        var changed = account with { PasswordHash = _hasher.Hash(newPassword!), PasswordChangedAtUtc = UtcNow };

        lock (_gate)
        {
            Save(changed);
            _account = changed;

            foreach (var token in _sessions.Keys.Where(token => token != session.Token).ToList())
                _sessions.Remove(token);

            _options.Audit($"password changed by '{account.Username}'; other browsers signed out");
        }
    }

    /// <summary>
    /// Forgets the account — the way back in for an owner who lost the password. Every browser is
    /// signed out and a new setup code is made and returned.
    /// </summary>
    public string Reset()
    {
        lock (_gate)
        {
            var path = Path.GetFullPath(_options.CredentialsPath);
            if (File.Exists(path))
                File.Delete(path);

            _account = null;
            _sessions.Clear();
            _setupCode = NewSetupCode();
            _loaded = true;
            _options.Audit("reset from the console; every browser signed out");
            return _setupCode;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void EnsureLoaded()
    {
        if (_loaded)
            return;

        var path = Path.GetFullPath(_options.CredentialsPath);
        _account = null;

        if (File.Exists(path))
        {
            try
            {
                _account = JsonSerializer.Deserialize<DashboardAccount>(File.ReadAllText(path), FileFormat);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                throw new InvalidOperationException($"The dashboard's account file '{path}' cannot be read: {ex.Message}", ex);
            }

            if (_account is not { Username.Length: > 0, PasswordHash.Length: > 0 })
                throw new InvalidOperationException(
                    $"The dashboard's account file '{path}' does not hold an account. Delete it to set the dashboard up again.");
        }

        _setupCode = _account is null ? _setupCode ?? NewSetupCode() : null;
        _loaded = true;
    }

    private DashboardSession OpenSession(string username)
    {
        var now = UtcNow;

        // A bounded number of browsers: the oldest sign-in makes room for the newest.
        while (_sessions.Count >= MaximumSessions)
            _sessions.Remove(_sessions.MinBy(entry => entry.Value.CreatedAtUtc).Key);

        var session = new DashboardSession(
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            username,
            now,
            Min(now + _options.SessionIdleTimeout, now + _options.SessionLifetime));

        _sessions[session.Token] = session;
        return session;
    }

    private void Save(DashboardAccount account)
    {
        var path = Path.GetFullPath(_options.CredentialsPath);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        // Written beside the real file and moved over it, so a crash mid-write cannot leave an account
        // nobody can sign in to; readable by this server's account alone.
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var file = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            file.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        try
        {
            using (var stream = new FileStream(temporary, file))
                JsonSerializer.Serialize(stream, account, FileFormat);

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }

    private static string CheckUsername(string? username)
    {
        var name = (username ?? "").Trim();

        if (name.Length is 0 or > MaximumUsernameLength || name.Any(char.IsControl))
            throw ApiException.BadRequest("invalid_username", $"Choose a username of 1 to {MaximumUsernameLength} characters.");

        return name;
    }

    private static void CheckNewPassword(string? password)
    {
        if (password is null || password.Length < DashboardOptions.MinimumPasswordLength)
            throw ApiException.BadRequest("weak_password",
                $"Choose a password of at least {DashboardOptions.MinimumPasswordLength} characters.");

        if (password.Length > MaximumPasswordLength)
            throw ApiException.BadRequest("weak_password", $"A password may be at most {MaximumPasswordLength} characters.");
    }

    /// <summary>Twelve characters from an alphabet without look-alikes, shown as XXXX-XXXX-XXXX.</summary>
    private static string NewSetupCode()
    {
        var characters = RandomNumberGenerator.GetItems<char>(CodeAlphabet, 12);
        return $"{new string(characters, 0, 4)}-{new string(characters, 4, 4)}-{new string(characters, 8, 4)}";
    }

    /// <summary>Compares codes in constant time, forgiving case, spaces and dashes.</summary>
    private static bool CodeMatches(string? given, string expected)
    {
        static byte[] Normalise(string code) =>
            Encoding.ASCII.GetBytes(new string(code.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).ToArray()));

        return CryptographicOperations.FixedTimeEquals(Normalise(given ?? ""), Normalise(expected));
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
