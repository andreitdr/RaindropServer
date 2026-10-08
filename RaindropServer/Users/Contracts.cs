namespace RaindropServer.Users;

// ── Shared shapes ────────────────────────────────────────────────────────────

/// <summary>The caller's own account, returned by every call that establishes or inspects a session.</summary>
/// <param name="UserId">The UUID this user is known by on this server.</param>
/// <param name="Username">The username, unique on this server.</param>
/// <param name="State">One of <c>active</c>, <c>left_preserved</c>, <c>erased</c>.</param>
/// <param name="CreatedAtUtc">When this UUID joined the server.</param>
/// <param name="LastLoginAtUtc">When the user last logged in, if ever.</param>
public record UserProfile(
    Guid UserId,
    string Username,
    string State,
    DateTime CreatedAtUtc,
    DateTime? LastLoginAtUtc);

/// <summary>What another member is allowed to see about a UUID, e.g. to render a message author.</summary>
/// <param name="UserId">The UUID the messages are linked to.</param>
/// <param name="Username">The username, or null once the account has been erased.</param>
/// <param name="State">One of <c>unregistered</c>, <c>active</c>, <c>left_preserved</c>, <c>erased</c>.</param>
public record PublicUserProfile(Guid UserId, string? Username, string State);

/// <summary>A freshly opened session plus the account it belongs to.</summary>
/// <param name="User">The logged-in account.</param>
/// <param name="Token">The bearer token to send as <c>Authorization: Bearer &lt;token&gt;</c>.</param>
/// <param name="ExpiresAtUtc">When the session lapses if the client goes idle.</param>
public record SessionResponse(UserProfile User, string Token, DateTime ExpiresAtUtc);

/// <param name="Message">A human-readable result for operations that return nothing else.</param>
public record MessageResponse(string Message);

// ── Requests ─────────────────────────────────────────────────────────────────

/// <param name="UserId">The UUID to ask about.</param>
public record UserStateRequest(Guid UserId);

/// <param name="Username">The username to check for availability.</param>
public record UsernameAvailabilityRequest(string Username);

/// <param name="UserId">The UUID to resolve to a display name.</param>
public record LookupUserRequest(Guid UserId);

/// <param name="UserId">The GUID the client generated for this server.</param>
/// <param name="Username">The username to claim; must be unused on this server.</param>
/// <param name="Password">The password that will guard this UUID from now on.</param>
public record RegisterRequest(Guid UserId, string Username, string Password);

/// <param name="UserId">The UUID to log in as. Optional if <paramref name="Username"/> is given.</param>
/// <param name="Username">An alternative to <paramref name="UserId"/> for clients that lost their UUID.</param>
/// <param name="Password">The account password.</param>
public record LoginRequest(Guid? UserId, string? Username, string Password);

/// <param name="UserId">The UUID of the preserved account to restore. Optional if <paramref name="Username"/> is given.</param>
/// <param name="Username">
/// An alternative to <paramref name="UserId"/> for clients that lost their UUID — the username stays
/// reserved for as long as the account is preserved, so it still finds the account.
/// </param>
/// <param name="Password">The password the account had when it left.</param>
public record RejoinRequest(Guid? UserId, string? Username, string Password);

/// <param name="CurrentPassword">The password in use today, as a confirmation of identity.</param>
/// <param name="NewPassword">The replacement password.</param>
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <param name="Username">The new username to claim; must be unused on this server.</param>
public record ChangeUsernameRequest(string Username);

/// <param name="Password">The account password, confirming this irreversible request.</param>
/// <param name="PreserveData">
/// True to keep the username and password for a possible return (erased automatically once the
/// retention window closes); false to erase them immediately. Messages stay either way — they are
/// linked to the UUID, not to the account.
/// </param>
public record LeaveRequest(string Password, bool PreserveData);

// ── Responses ────────────────────────────────────────────────────────────────

/// <summary>Tells a client which flow to show for a UUID before it has any credentials to offer.</summary>
/// <param name="UserId">The UUID that was asked about.</param>
/// <param name="State">
/// <c>unregistered</c> or <c>erased</c> — send the user through registration; <c>active</c> — ask for
/// the password; <c>left_preserved</c> — offer to rejoin.
/// </param>
/// <param name="Username">The username held by this UUID, when it still has one.</param>
/// <param name="DataExpiresAtUtc">When a preserved account will be erased, if it is preserved.</param>
public record UserStateResponse(Guid UserId, string State, string? Username, DateTime? DataExpiresAtUtc);

/// <param name="Username">The username that was checked.</param>
/// <param name="Available">Whether it can be claimed right now on this server.</param>
/// <param name="Reason">Why it cannot be claimed, when it cannot.</param>
public record UsernameAvailabilityResponse(string Username, bool Available, string? Reason);

/// <param name="State">The state the account was left in: <c>left_preserved</c> or <c>erased</c>.</param>
/// <param name="DataExpiresAtUtc">When preserved data will be erased, if it was preserved.</param>
/// <param name="Message">A human-readable summary of what was kept and what was deleted.</param>
public record LeaveResponse(string State, DateTime? DataExpiresAtUtc, string Message);
