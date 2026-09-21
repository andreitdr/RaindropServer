using RaindropServer.Channels;
using RaindropServer.Users;

namespace RaindropServer.Roles;

/// <summary>
/// Turns the roles a member holds into what they may do. The union of every role's permissions, plus
/// the <see cref="RoleKind.Everyone"/> role that every member holds without being given it.
/// </summary>
/// <remarks>
/// Roles are server-wide, so <paramref name="channel"/> only changes the answer for the one rule
/// that is about a particular channel: whoever created a channel moderates it, unless the server
/// owner has turned that off with <see cref="RoleOptions.ChannelCreatorModeratesOwnChannels"/>.
/// Per-channel role overrides would go here too, and nothing outside this class would change.
/// </remarks>
public sealed class RolePermissionResolver : IPermissionResolver
{
    /// <summary>What creating a channel lets you do in it, on top of whatever your roles allow.</summary>
    private const Permission Creator =
        Permission.ViewChannel | Permission.RenameChannels | Permission.DeleteChannels | Permission.ManageMessages;

    private readonly IRoleStore _roles;
    private readonly IUserStore _users;
    private readonly RoleOptions _options;
    private readonly RolePermissionCache _cache;

    public RolePermissionResolver(
        IRoleStore roles, IUserStore users, RoleOptions options, RolePermissionCache cache)
    {
        _roles = roles;
        _users = users;
        _options = options;
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

        var granted = (await GetServerWideAsync(userId, cancellationToken)).Expand();

        if (granted.HasFlag(Permission.Administrator))
            return granted;

        if (channel is not null && channel.CreatedBy == userId && _options.ChannelCreatorModeratesOwnChannels)
            granted |= Creator;

        return granted;
    }

    /// <summary>
    /// The union of every role a member holds, before any channel-specific rule and before
    /// <see cref="Permission.Administrator"/> is expanded. Cached, because a channel list asks for it
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
