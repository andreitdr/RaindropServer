namespace RaindropServer.Roles;

// ── Shared shapes ────────────────────────────────────────────────────────────

/// <summary>
/// A role as a client sees it. Permissions travel as names rather than as a bitfield, so a client
/// too old to know one lists what it does understand and ignores the rest.
/// </summary>
/// <param name="RoleId">The server-generated id to pass back on every call about this role.</param>
/// <param name="Name">The role name, unique on this server.</param>
/// <param name="Kind">
/// <c>custom</c>, <c>everyone</c> or <c>owner</c> — see <see cref="RoleKindNames"/>. The two built-in
/// kinds cannot be deleted, and <c>everyone</c> is never handed out because every member has it.
/// </param>
/// <param name="Position">Where the role sits in the list, highest first. Presentation only.</param>
/// <param name="Permissions">What holding this role allows, as <see cref="PermissionNames"/> values.</param>
/// <param name="CreatedAtUtc">When the role was created.</param>
public record RoleSummary(
    Guid RoleId,
    string Name,
    string Kind,
    int Position,
    IReadOnlyList<string> Permissions,
    DateTime CreatedAtUtc);

// ── Requests ─────────────────────────────────────────────────────────────────

/// <param name="Name">The name to claim; must be unused on this server.</param>
/// <param name="Permissions">
/// What the role grants, as <see cref="PermissionNames"/> values. Left out, the role grants nothing
/// and is a label until somebody edits it.
/// </param>
/// <param name="Position">Where it sits in the list; left out, it goes above every other custom role.</param>
public record CreateRoleRequest(string Name, IReadOnlyList<string>? Permissions = null, int? Position = null);

/// <summary>
/// Edits a role. Every field but the id is optional and means "leave this alone" when left out — so
/// renaming a role cannot accidentally clear its permissions. Sending an empty permission list
/// <em>does</em> clear them; that is the difference between absent and empty.
/// </summary>
/// <param name="RoleId">The role to edit.</param>
/// <param name="Name">A new name, or null to keep the current one.</param>
/// <param name="Permissions">A replacement permission set, or null to keep the current one.</param>
/// <param name="Position">A new position, or null to keep the current one.</param>
public record UpdateRoleRequest(
    Guid RoleId,
    string? Name = null,
    IReadOnlyList<string>? Permissions = null,
    int? Position = null);

/// <param name="RoleId">The role being asked about.</param>
public record RoleRequest(Guid RoleId);

/// <param name="UserId">The member being asked about.</param>
public record MemberRolesRequest(Guid UserId);

/// <param name="UserId">The member to grant the role to, or take it from.</param>
/// <param name="RoleId">The role in question.</param>
public record MemberRoleRequest(Guid UserId, Guid RoleId);

// ── Responses ────────────────────────────────────────────────────────────────

/// <param name="Roles">Every role on this server, highest position first.</param>
public record RoleListResponse(IReadOnlyList<RoleSummary> Roles);

/// <param name="Role">The role itself.</param>
/// <param name="Members">
/// The UUIDs holding it, in the order they were granted it. Empty for the <c>everyone</c> role,
/// which nobody is assigned because every active member has it.
/// </param>
public record RoleDetail(RoleSummary Role, IReadOnlyList<Guid> Members);

/// <summary>What one member may do here, and which roles say so.</summary>
/// <param name="UserId">The member this describes.</param>
/// <param name="Roles">
/// The roles they have been granted, highest position first. The <c>everyone</c> role is included
/// first, even though nobody is assigned it, because it is part of the answer.
/// </param>
/// <param name="Permissions">
/// What they may actually do across the server, as <see cref="PermissionNames"/> values: the union
/// of every role above, fully expanded, so an administrator lists everything.
/// </param>
public record MemberRolesResponse(Guid UserId, IReadOnlyList<RoleSummary> Roles, IReadOnlyList<string> Permissions);

/// <param name="RoleId">The role that was deleted.</param>
/// <param name="Message">A human-readable summary of what went with it.</param>
public record RoleDeletedResponse(Guid RoleId, string Message);

/// <param name="UserId">The member whose roles changed.</param>
/// <param name="RoleId">The role that was granted or taken away.</param>
/// <param name="Changed">
/// Whether anything actually changed: false when the member already held the role, or already did
/// not. Not an error either way — the request describes a state, and that state now holds.
/// </param>
/// <param name="Message">A human-readable summary.</param>
public record MemberRoleChangedResponse(Guid UserId, Guid RoleId, bool Changed, string Message);

/// <param name="Permissions">
/// Every permission this server understands, in the order a permission editor should list them.
/// A client builds its role editor from this rather than from a hard-coded list, so a server running
/// a newer build offers the permissions it has.
/// </param>
public record PermissionCatalogueResponse(IReadOnlyList<PermissionDescription> Permissions);
