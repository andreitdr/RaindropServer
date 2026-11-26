using RaindropServer.Channels;
using RaindropServer.Users;

namespace RaindropServer.Roles;

/// <summary>
/// Turns the roles a member holds into what they may do. The union of every role's permissions, plus
/// the <see cref="RoleKind.Everyone"/> role that every member holds without being given it.
/// </summary>
/// <remarks>
/// Roles are the only source of permissions: nothing is granted to a person directly, and creating a
/// channel gives its creator nothing a role does not. Roles are server-wide, so the channel passed in
/// does not change the answer today; it is the seam per-channel role overrides would use, and nothing
/// outside this class would change.
/// </remarks>
public sealed class RolePermissionResolver : IPermissionResolver
{
    private readonly IRoleStore _roles;
    private readonly IUserStore _users;
    private readonly RolePermissionCache _cache;

    public RolePermissionResolver(IRoleStore roles, IUserStore users, RolePermissionCache cache)
    {
        _roles = roles;
        _users = users;
        _cache = cache;
    }

    /// <inheritdoc />
    public async Task<Permission> GetAsync(
        Guid userId, ChannelRecord? channel, CancellationToken cancellationToken)
    {
        // Sessions outlive the account behind them — a member can leave in one session while another
        // still holds a token — so membership is re-checked here rather than trusted from the token.
        // It is also what makes role assignments safe to keep after somebody leaves: they grant
        // nothing until the account is active again.
        var user = await _users.FindByIdAsync(userId, cancellationToken);

        if (user is null || user.Status != UserStatus.Active)
            return Permission.None;

        return (await GetServerWideAsync(userId, cancellationToken)).Expand();
    }

    /// <summary>
    /// The union of every role a member holds, before <see cref="Permission.Administrator"/> is
    /// expanded. Cached, because a channel list asks for it
    /// once per channel.
    /// </summary>
    internal async Task<Permission> GetServerWideAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (_cache.TryGet(userId, out var cached))
            return cached;

        // Everyone is not an assignment, so it is read separately and folded in for every member.
        var everyone = await _roles.FindByKindAsync(RoleKind.Everyone, cancellationToken);
        var permissions = everyone?.Permissions ?? Permission.None;

        foreach (var role in await _roles.ListForUserAsync(userId, cancellationToken))
            permissions |= role.Permissions;

        _cache.Set(userId, permissions);
        return permissions;
    }
}
