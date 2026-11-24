using RaindropServer.E2ee;
using RaindropServer.Events;
using RaindropServer.Roles;
using RaindropServer.Users;

namespace RaindropServer.Dashboard;

/// <summary>
/// An account as the owner sees it before issuing a recovery code — enough to be sure it is the
/// person in front of them.
/// </summary>
public sealed record AccountSummary(
    Guid UserId,
    string Username,
    string State,
    DateTime JoinedAtUtc,
    DateTime? LastSignInAtUtc,
    DateTime? LeftAtUtc,
    IReadOnlyList<string> Roles,
    int? Devices,
    bool Online,
    PendingClaim? Recovery);

/// <summary>
/// Account recovery from the dashboard: the page's face of the console's <c>reclaim</c>. A member who
/// lost every device, their recovery words and any backup gives the owner their username or account id;
/// the owner, sure it is really them, issues the same one-time claim code
/// (<see cref="AccountClaimService"/>) that their new identity redeems when it joins.
/// </summary>
public sealed class DashboardRecovery
{
    private readonly IUserStore _users;
    private readonly IRoleStore _roles;
    private readonly IDeviceStore? _devices;
    private readonly IEventConnectionRegistry _connections;
    private readonly AccountClaimService _claims;
    private readonly DashboardOptions _options;

    public DashboardRecovery(
        IUserStore users,
        IRoleStore roles,
        IEventConnectionRegistry connections,
        AccountClaimService claims,
        DashboardOptions options,
        IDeviceStore? devices = null)
    {
        _users = users;
        _roles = roles;
        _devices = devices;
        _connections = connections;
        _claims = claims;
        _options = options;
    }

    /// <summary>
    /// Finds the account <paramref name="query"/> names — an account id, or a username with or without
    /// a leading @. Erased accounts are not found: there is nothing left of them to hand over.
    /// </summary>
    public async Task<AccountSummary> LookupAsync(string? query, CancellationToken cancellationToken)
    {
        var text = (query ?? "").Trim().TrimStart('@');
        if (text.Length == 0)
            throw ApiException.BadRequest("invalid_query", "Type the member's username or account id.");

        var user = Guid.TryParse(text, out var id)
            ? await _users.FindByIdAsync(id, cancellationToken)
            : await _users.FindByUsernameAsync(text.ToLowerInvariant(), cancellationToken);

        if (user is not { Status: UserStatus.Active or UserStatus.LeftDataPreserved, Username: { } name })
            throw ApiException.NotFound("user_not_found", $"No member here is '{text}'.");

        var roles = await _roles.ListForUserAsync(user.UserId, cancellationToken);

        int? devices;
        try
        {
            devices = _devices is null
                ? null
                : (await _devices.ListDevicesAsync([user.UserId], includeRevoked: false, cancellationToken)).Count;
        }
        catch (NotSupportedException)
        {
            devices = null;
        }

        return new AccountSummary(
            user.UserId,
            name,
            user.Status == UserStatus.Active ? "active" : "left_preserved",
            user.CreatedAtUtc,
            user.LastLoginAtUtc,
            user.LeftAtUtc,
            roles.Select(role => role.Name).ToList(),
            devices,
            _connections.ConnectedUserIds().Contains(user.UserId),
            _claims.ListPending().FirstOrDefault(claim => claim.UserId == user.UserId));
    }

    /// <summary>Issues a recovery code for the account, replacing any earlier one.</summary>
    public async Task<AccountClaim> IssueAsync(Guid userId, string owner, CancellationToken cancellationToken)
    {
        var claim = await _claims.IssueAsync(userId, cancellationToken);
        _options.Audit($"'{owner}' issued a recovery code for {claim.Username} ({claim.UserId:D}), good until {claim.ExpiresAtUtc:u}");
        return claim;
    }

    /// <summary>The codes that still work.</summary>
    public IReadOnlyList<PendingClaim> Pending() => _claims.ListPending();

    /// <summary>Withdraws the code for an account.</summary>
    public void Cancel(Guid userId, string owner)
    {
        var pending = _claims.ListPending().FirstOrDefault(claim => claim.UserId == userId);

        if (!_claims.Cancel(userId))
            throw ApiException.NotFound("claim_not_found", "That account has no recovery code waiting.");

        _options.Audit($"'{owner}' cancelled the recovery code for {pending?.Username ?? userId.ToString("D")}");
    }
}
