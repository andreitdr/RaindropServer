namespace RaindropServer.Users;

/// <summary>
/// A logged-in client. Sessions live in memory only: they are deliberately not persisted, so a
/// server restart logs everyone out, matching the rule that the login state lasts no longer than
/// the client's connection.
/// </summary>
/// <param name="Token">The bearer token the client sends on every authenticated request.</param>
/// <param name="UserId">The UUID of the logged-in user on this server.</param>
/// <param name="Username">The user's current username, kept here so callers avoid a lookup.</param>
/// <param name="CreatedAtUtc">When the session was opened.</param>
/// <param name="ExpiresAtUtc">When the session lapses if the client stops making requests.</param>
/// <param name="DeviceId">
/// The end-to-end encryption device this session proved it runs on (see
/// <c>E2ee/DeviceService.BindSessionAsync</c>), or null until it has. A password alone opens a
/// session; only a device's key binds one, and some operations require that.
/// </param>
public sealed record UserSession(
    string Token,
    Guid UserId,
    string Username,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    Guid? DeviceId = null);
