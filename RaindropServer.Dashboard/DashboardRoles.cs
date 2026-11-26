using RaindropServer.Roles;
using RaindropServer.Users;

namespace RaindropServer.Dashboard;

/// <summary>A role as the dashboard lists it: the role, and how many members hold it.</summary>
public sealed record DashboardRole(RoleSummary Role, int Members);

/// <summary>One member holding a role, with the name to show for them.</summary>
public sealed record DashboardRoleMember(Guid UserId, string? Username);

/// <summary>A role and everyone holding it, for the dashboard's role editor.</summary>
public sealed record DashboardRoleDetail(RoleSummary Role, IReadOnlyList<DashboardRoleMember> Members);

/// <param name="Name">The name to claim.</param>
/// <param name="Permissions">What it grants, as permission names. Left out, nothing.</param>
/// <param name="Position">Where it sits in the list. Left out, above every other custom role.</param>
public sealed record DashboardCreateRole(string? Name, IReadOnlyList<string>? Permissions, int? Position);

/// <summary>An edit. Every field left out is left alone; an empty permission list clears them.</summary>
public sealed record DashboardUpdateRole(string? Name, IReadOnlyList<string>? Permissions, int? Position);

/// <summary>
/// Roles from the dashboard: the page's face of <see cref="RoleService"/>'s console methods. Whoever is
/// signed in to the dashboard owns the server, so nothing here checks a permission — but every rule
/// about the roles themselves still holds (the owner role is immutable, the everyone role keeps its
/// name, the last owner keeps the owner role), and members' clients hear about every change exactly as
/// they would had a member made it. Each change is written to the dashboard's audit log.
/// </summary>
public sealed class DashboardRoles
{
    private readonly RoleService _roles;
    private readonly IUserStore _users;
    private readonly DashboardOptions _options;

    public DashboardRoles(RoleService roles, IUserStore users, DashboardOptions options)
    {
        _roles = roles;
        _users = users;
        _options = options;
    }

    /// <summary>Every permission this build understands, in the order an editor lists them.</summary>
    public static IReadOnlyList<PermissionDescription> Permissions() => RoleService.Catalogue().Permissions;

    /// <summary>Every role, highest position first, with how many members hold each.</summary>
    public async Task<IReadOnlyList<DashboardRole>> ListAsync(CancellationToken cancellationToken)
    {
        var roles = await _roles.ListForConsoleAsync(cancellationToken);
        var listed = new List<DashboardRole>(roles.Count);

        foreach (var role in roles)
        {
            var detail = await _roles.GetForConsoleAsync(role.RoleId, cancellationToken);
            listed.Add(new DashboardRole(detail.Role, detail.Members.Count));
        }

        return listed;
    }

    /// <summary>One role and the members holding it, named.</summary>
    public async Task<DashboardRoleDetail> GetAsync(Guid roleId, CancellationToken cancellationToken)
    {
        var detail = await _roles.GetForConsoleAsync(roleId, cancellationToken);
        var members = new List<DashboardRoleMember>(detail.Members.Count);

        foreach (var userId in detail.Members)
        {
            var user = await _users.FindByIdAsync(userId, cancellationToken);
            members.Add(new DashboardRoleMember(userId, user?.Username));
        }

        return new DashboardRoleDetail(detail.Role, members);
    }

    public async Task<RoleSummary> CreateAsync(DashboardCreateRole request, string owner, CancellationToken cancellationToken)
    {
        var role = await _roles.CreateFromConsoleAsync(
            new CreateRoleRequest(request.Name ?? string.Empty, request.Permissions, request.Position), cancellationToken);

        _options.Audit($"'{owner}' created the role '{role.Name}' ({Describe(role.Permissions)})");
        return role;
    }

    public async Task<RoleSummary> UpdateAsync(
        Guid roleId, DashboardUpdateRole request, string owner, CancellationToken cancellationToken)
    {
        var role = await _roles.UpdateFromConsoleAsync(
            new UpdateRoleRequest(roleId, request.Name, request.Permissions, request.Position), cancellationToken);

        _options.Audit($"'{owner}' changed the role '{role.Name}' ({Describe(role.Permissions)})");
        return role;
    }

    public async Task<RoleDeletedResponse> DeleteAsync(Guid roleId, string owner, CancellationToken cancellationToken)
    {
        var deleted = await _roles.DeleteFromConsoleAsync(new RoleRequest(roleId), cancellationToken);

        _options.Audit($"'{owner}': {deleted.Message}");
        return deleted;
    }

    public async Task<MemberRoleChangedResponse> AssignAsync(
        Guid userId, Guid roleId, string owner, CancellationToken cancellationToken)
    {
        var changed = await _roles.AssignFromConsoleAsync(new MemberRoleRequest(userId, roleId), cancellationToken);

        if (changed.Changed)
            _options.Audit($"'{owner}': {changed.Message}");

        return changed;
    }

    public async Task<MemberRoleChangedResponse> UnassignAsync(
        Guid userId, Guid roleId, string owner, CancellationToken cancellationToken)
    {
        var changed = await _roles.UnassignFromConsoleAsync(new MemberRoleRequest(userId, roleId), cancellationToken);

        if (changed.Changed)
            _options.Audit($"'{owner}': {changed.Message}");

        return changed;
    }

    private static string Describe(IReadOnlyList<string> permissions) =>
        permissions.Count == 0 ? "no permissions" : string.Join(", ", permissions);
}
