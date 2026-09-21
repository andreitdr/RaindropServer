using RaindropServer.Events;

namespace RaindropServer.Users;

/// <summary>
/// The whole user lifecycle on one server: claiming a username, logging in and out, changing
/// credentials, and leaving — with or without keeping your data. Every rule lives here, so the
/// endpoints stay thin and the database driver stays dumb.
/// </summary>
public sealed class UserService
{
    private readonly IUserStore _store;
    private readonly IPasswordHasher _hasher;
    private readonly ISessionStore _sessions;
    private readonly UserOptions _options;
    private readonly IEventPublisher _events;
    private readonly IReadOnlyList<IRegistrationListener> _listeners;
    private readonly IReadOnlyList<ISessionListener> _sessionListeners;
    private readonly TimeProvider _time;

    public UserService(
        IUserStore store,
        IPasswordHasher hasher,
        ISessionStore sessions,
        UserOptions options,
        IEventPublisher events,
        IEnumerable<IRegistrationListener>? listeners = null,
        IEnumerable<ISessionListener>? sessionListeners = null,
        TimeProvider? time = null)
    {
        _store = store;
        _hasher = hasher;
        _sessions = sessions;
        _options = options;
        _events = events;
        _listeners = listeners?.ToList() ?? [];
        _sessionListeners = sessionListeners?.ToList() ?? [];
        _time = time ?? TimeProvider.System;
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    // ── Discovery: what should the client show for this UUID? ─────────────────

    /// <summary>
    /// Reports what a UUID means to this server so a freshly connected client knows whether to ask
    /// for a new username, a password, or an offer to rejoin.
    /// </summary>
    public async Task<UserStateResponse> GetStateAsync(Guid userId, CancellationToken cancellationToken)
    {
        RequireUserId(userId);

        var user = await _store.FindByIdAsync(userId, cancellationToken);

        if (user is null)
            return new UserStateResponse(userId, StateNames.Unregistered, null, null);

        return new UserStateResponse(userId, StateNames.Of(user.Status), user.Username, user.DataExpiresAtUtc);
    }

    /// <summary>Checks whether a username can be claimed, applying the same rules registration will.</summary>
    public async Task<UsernameAvailabilityResponse> CheckUsernameAsync(string? username, CancellationToken cancellationToken)
    {
        string validated;
        try
        {
            validated = UsernamePolicy.Validate(username);
        }
        catch (ApiException ex)
        {
            return new UsernameAvailabilityResponse(username?.Trim() ?? string.Empty, false, ex.Message);
        }

        var taken = await _store.UsernameExistsAsync(UsernamePolicy.Normalize(validated), cancellationToken);

        return new UsernameAvailabilityResponse(
            validated,
            !taken,
            taken ? "That username is already taken on this server." : null);
    }

    /// <summary>Returns what other members may see about a UUID, e.g. when rendering its messages.</summary>
    public async Task<PublicUserProfile> LookupAsync(Guid userId, CancellationToken cancellationToken)
    {
        RequireUserId(userId);

        var user = await _store.FindByIdAsync(userId, cancellationToken);

        if (user is null)
            return new PublicUserProfile(userId, null, StateNames.Unregistered);

        return new PublicUserProfile(userId, user.Username, StateNames.Of(user.Status));
    }

    // ── First login ──────────────────────────────────────────────────────────

    /// <summary>
    /// First login: binds a username and password to a UUID and opens a session. A UUID whose account
    /// was erased may register again from scratch — the tombstone row is reused so its messages keep
    /// pointing at a real membership.
    /// </summary>
    public async Task<SessionResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        RequireUserId(request.UserId);
        var username = UsernamePolicy.Validate(request.Username);
        ValidatePassword(request.Password);

        var existing = await _store.FindByIdAsync(request.UserId, cancellationToken);

        switch (existing?.Status)
        {
            case UserStatus.Active:
                throw ApiException.Conflict("already_registered",
                    "This UUID already has an account on this server. Log in instead.");

            case UserStatus.LeftDataPreserved:
                throw ApiException.Conflict("account_preserved",
                    "This UUID left the server with its data preserved. Rejoin with your old password instead.");
        }

        var normalized = UsernamePolicy.Normalize(username);
        await RequireUsernameFreeAsync(normalized, cancellationToken);

        var now = UtcNow;
        var user = new UserRecord
        {
            UserId = request.UserId,
            Username = username,
            UsernameNormalized = normalized,
            PasswordHash = _hasher.Hash(request.Password),
            Status = UserStatus.Active,
            CreatedAtUtc = now,
            LastLoginAtUtc = now
        };

        if (existing is null)
            await _store.InsertAsync(user, cancellationToken);
        else
            await _store.UpdateAsync(user, cancellationToken);

        await AnnounceRegistrationAsync(user, cancellationToken);
        await _events.PublishAsync(ServerEventTypes.MemberJoined, new MemberEvent(user.UserId, user.Username));

        return await OpenSessionAsync(user, cancellationToken);
    }

    // ── Returning ────────────────────────────────────────────────────────────

    /// <summary>Logs an existing member in by UUID (or by username, for a client that lost its UUID).</summary>
    public async Task<SessionResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        if (request.UserId is null && string.IsNullOrWhiteSpace(request.Username))
            throw ApiException.BadRequest("missing_identifier", "Provide either a userId or a username to log in.");

        var user = request.UserId is { } id
            ? await _store.FindByIdAsync(id, cancellationToken)
            : await _store.FindByUsernameAsync(UsernamePolicy.Normalize(request.Username!), cancellationToken);

        // A preserved account is the one failure worth naming: the client has to be told to rejoin.
        if (user?.Status == UserStatus.LeftDataPreserved)
            throw ApiException.Forbidden("account_left",
                "This account left the server. Rejoin to restore it.");

        if (user is null || user.Status != UserStatus.Active || user.PasswordHash is null
            || !_hasher.Verify(request.Password ?? string.Empty, user.PasswordHash))
        {
            // Anything else is reported identically, so the API cannot be used to enumerate UUIDs.
            throw ApiException.Unauthorized("invalid_credentials", "The UUID, username or password is incorrect.");
        }

        var loggedIn = user with { LastLoginAtUtc = UtcNow };
        await _store.UpdateAsync(loggedIn, cancellationToken);

        return await OpenSessionAsync(loggedIn, cancellationToken);
    }

    /// <summary>
    /// Restores an account that left with its data preserved, provided the retention window has not
    /// closed. The username was held in reserve for exactly this, so it comes back with the account.
    /// </summary>
    public async Task<SessionResponse> RejoinAsync(RejoinRequest request, CancellationToken cancellationToken)
    {
        RequireUserId(request.UserId);

        var user = await _store.FindByIdAsync(request.UserId, cancellationToken);

        if (user is null || user.Status == UserStatus.Erased)
            throw ApiException.NotFound("no_preserved_account",
                "There is no preserved account for this UUID. Register a new one.");

        if (user.Status == UserStatus.Active)
            throw ApiException.Conflict("already_active", "This account is already a member. Log in instead.");

        if (user.PasswordHash is null || !_hasher.Verify(request.Password ?? string.Empty, user.PasswordHash))
            throw ApiException.Unauthorized("invalid_credentials", "The password is incorrect.");

        var now = UtcNow;
        var rejoined = user with
        {
            Status = UserStatus.Active,
            LastLoginAtUtc = now,
            LeftAtUtc = null,
            DataExpiresAtUtc = null
        };

        await _store.UpdateAsync(rejoined, cancellationToken);
        await _events.PublishAsync(ServerEventTypes.MemberJoined, new MemberEvent(rejoined.UserId, rejoined.Username));

        return await OpenSessionAsync(rejoined, cancellationToken);
    }

    /// <summary>Ends this one session. Other devices logged in as the same user stay logged in.</summary>
    public async Task<MessageResponse> LogoutAsync(UserSession session, CancellationToken cancellationToken)
    {
        _sessions.Remove(session.Token);
        await AnnounceSessionsChangedAsync(session.UserId, cancellationToken);

        return new MessageResponse("Logged out.");
    }

    // ── Managing the account ─────────────────────────────────────────────────

    /// <summary>Returns the caller's own account.</summary>
    public async Task<UserProfile> GetProfileAsync(UserSession session, CancellationToken cancellationToken)
        => ToProfile(await RequireActiveAsync(session, cancellationToken));

    /// <summary>
    /// Replaces the password after confirming the current one, then re-issues the session: every
    /// session that was opened with the old password is dropped, including on other devices.
    /// </summary>
    public async Task<SessionResponse> ChangePasswordAsync(
        UserSession session, ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var user = await RequireActiveAsync(session, cancellationToken);

        if (user.PasswordHash is null || !_hasher.Verify(request.CurrentPassword ?? string.Empty, user.PasswordHash))
            throw ApiException.Unauthorized("invalid_credentials", "The current password is incorrect.");

        ValidatePassword(request.NewPassword);

        var updated = user with { PasswordHash = _hasher.Hash(request.NewPassword) };
        await _store.UpdateAsync(updated, cancellationToken);

        // One announcement for both halves: the old sessions going and the new one arriving are a
        // single change as far as anybody listening is concerned.
        _sessions.RemoveAllForUser(user.UserId);
        return await OpenSessionAsync(updated, cancellationToken);
    }

    /// <summary>Claims a different username, subject to the same uniqueness rule as registration.</summary>
    public async Task<UserProfile> ChangeUsernameAsync(
        UserSession session, ChangeUsernameRequest request, CancellationToken cancellationToken)
    {
        var user = await RequireActiveAsync(session, cancellationToken);
        var username = UsernamePolicy.Validate(request.Username);
        var normalized = UsernamePolicy.Normalize(username);

        // Re-casing your own name is not a claim on a new one, so skip the uniqueness check for it.
        if (!string.Equals(normalized, user.UsernameNormalized, StringComparison.Ordinal))
            await RequireUsernameFreeAsync(normalized, cancellationToken);

        var updated = user with { Username = username, UsernameNormalized = normalized };
        await _store.UpdateAsync(updated, cancellationToken);

        _sessions.RenameUser(user.UserId, username);
        await _events.PublishAsync(ServerEventTypes.MemberRenamed, new MemberEvent(updated.UserId, updated.Username));

        return ToProfile(updated);
    }

    // ── Leaving ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Leaves the server for good. With <see cref="LeaveRequest.PreserveData"/> the username and
    /// password are held — and the username stays reserved — until the retention window closes, at
    /// which point <see cref="RetentionCleanupService"/> erases them. Without it, they are erased on
    /// the spot. Either way the UUID row survives, because messages are linked to the UUID.
    /// </summary>
    public async Task<LeaveResponse> LeaveAsync(
        UserSession session, LeaveRequest request, CancellationToken cancellationToken)
    {
        var user = await RequireActiveAsync(session, cancellationToken);

        if (user.PasswordHash is null || !_hasher.Verify(request.Password ?? string.Empty, user.PasswordHash))
            throw ApiException.Unauthorized("invalid_credentials",
                "The password is incorrect. Leaving the server has to be confirmed with your password.");

        var now = UtcNow;
        UserRecord left;
        LeaveResponse response;

        if (request.PreserveData)
        {
            var expiresAt = now + _options.DataRetentionAfterLeaving;
            left = user with
            {
                Status = UserStatus.LeftDataPreserved,
                LeftAtUtc = now,
                DataExpiresAtUtc = expiresAt
            };

            response = new LeaveResponse(StateNames.LeftPreserved, expiresAt,
                $"You have left the server. Your username and password are kept until {expiresAt:u} " +
                "so you can rejoin, and are deleted automatically after that.");
        }
        else
        {
            left = user.AsErased(now);

            response = new LeaveResponse(StateNames.Erased, null,
                "You have left the server and your username and password have been deleted. " +
                "Messages you sent remain, linked to your UUID.");
        }

        await _store.UpdateAsync(left, cancellationToken);
        _sessions.RemoveAllForUser(user.UserId);

        await AnnounceSessionsChangedAsync(user.UserId, cancellationToken);
        await _events.PublishAsync(ServerEventTypes.MemberLeft, new MemberEvent(left.UserId, left.Username));

        return response;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Tells everything that wanted to know about a new member — the roles subsystem, which may hand
    /// this UUID the owner role. A listener that fails is reported and stepped over: the account row
    /// is already written, so refusing the session now would leave the client unable to log in to a
    /// server it has just joined.
    /// </summary>
    private async Task AnnounceRegistrationAsync(UserRecord user, CancellationToken cancellationToken)
    {
        foreach (var listener in _listeners)
        {
            try
            {
                await listener.OnRegisteredAsync(user, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine(
                    $"[raindrop] {listener.GetType().Name} failed for new member {user.UserId}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Tells everything that follows sessions — presence, the event stream — that this member's have
    /// changed. Stepped over on failure for the same reason registration listeners are: the change
    /// has already been made.
    /// </summary>
    private async Task AnnounceSessionsChangedAsync(Guid userId, CancellationToken cancellationToken)
    {
        foreach (var listener in _sessionListeners)
        {
            try
            {
                await listener.OnSessionsChangedAsync(userId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine(
                    $"[raindrop] {listener.GetType().Name} failed for member {userId}: {ex.Message}");
            }
        }
    }

    private async Task<SessionResponse> OpenSessionAsync(UserRecord user, CancellationToken cancellationToken)
    {
        var session = _sessions.Create(user.UserId, user.Username!);
        await AnnounceSessionsChangedAsync(user.UserId, cancellationToken);

        return new SessionResponse(ToProfile(user), session.Token, session.ExpiresAtUtc);
    }

    /// <summary>
    /// Re-reads the session's user on every authenticated call, so an account that left or was erased
    /// in another session cannot keep acting through a token that is still in memory.
    /// </summary>
    private async Task<UserRecord> RequireActiveAsync(UserSession session, CancellationToken cancellationToken)
    {
        var user = await _store.FindByIdAsync(session.UserId, cancellationToken);

        if (user is null || user.Status != UserStatus.Active)
        {
            _sessions.RemoveAllForUser(session.UserId);
            await AnnounceSessionsChangedAsync(session.UserId, cancellationToken);
            throw ApiException.Unauthorized("invalid_session", "This account is no longer a member of the server.");
        }

        return user;
    }

    private async Task RequireUsernameFreeAsync(string normalized, CancellationToken cancellationToken)
    {
        if (await _store.UsernameExistsAsync(normalized, cancellationToken))
            throw ApiException.Conflict("username_taken", "That username is already taken on this server.");
    }

    private static void RequireUserId(Guid userId)
    {
        if (userId == Guid.Empty)
            throw ApiException.BadRequest("invalid_user_id", "A non-empty UUID is required.");
    }

    private void ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < _options.MinimumPasswordLength)
            throw ApiException.BadRequest("weak_password",
                $"A password must be at least {_options.MinimumPasswordLength} characters long.");

        if (password.Length > _options.MaximumPasswordLength)
            throw ApiException.BadRequest("weak_password",
                $"A password may be at most {_options.MaximumPasswordLength} characters long.");
    }

    private static UserProfile ToProfile(UserRecord user) => new(
        user.UserId,
        user.Username ?? string.Empty,
        StateNames.Of(user.Status),
        user.CreatedAtUtc,
        user.LastLoginAtUtc);
}

/// <summary>The wire names for <see cref="UserStatus"/>, plus the state a UUID has before it registers.</summary>
public static class StateNames
{
    public const string Unregistered = "unregistered";
    public const string Active = "active";
    public const string LeftPreserved = "left_preserved";
    public const string Erased = "erased";

    public static string Of(UserStatus status) => status switch
    {
        UserStatus.Active => Active,
        UserStatus.LeftDataPreserved => LeftPreserved,
        UserStatus.Erased => Erased,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown user status.")
    };
}
