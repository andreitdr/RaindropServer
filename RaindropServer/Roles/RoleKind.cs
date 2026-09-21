namespace RaindropServer.Roles;

/// <summary>
/// Whether a role is one the server defines for itself or one a member made. The two built-in kinds
/// exist exactly once each and are created before the first member registers — see
/// <see cref="RoleService.EnsureBuiltInRolesAsync"/>.
/// </summary>
public enum RoleKind
{
    /// <summary>An ordinary role, created and deleted by whoever holds <see cref="Permission.ManageRoles"/>.</summary>
    Custom = 0,

    /// <summary>
    /// The role every active member holds without being given it — what an account can do here before
    /// anybody has granted it anything. Its permissions are editable; it cannot be deleted, and it is
    /// never assigned or unassigned, because holding it is what being a member means.
    /// </summary>
    Everyone = 1,

    /// <summary>
    /// The role that runs the server, holding <see cref="Permission.Administrator"/>. Granted
    /// automatically to the first UUID that registers, and neither editable nor deletable — a server
    /// whose owner role could be stripped of its permissions is a server nobody can administer.
    /// </summary>
    Owner = 2
}
