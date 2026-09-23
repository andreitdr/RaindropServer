using RaindropServer.Events;
using RaindropServer.Users;

namespace RaindropServer.Roles;

/// <summary>
/// Every rule about roles: what they are, who may change them, and who holds them. Endpoints stay
/// thin and the database module stays dumb, the same division <see cref="UserService"/> and
/// <see cref="Channels.ChannelService"/> follow.
/// </summary>
/// <remarks>
/// Two rules do most of the work here.
/// <list type="bullet">
/// <item>
/// The built-in roles are protected. <see cref="RoleKind.Owner"/> cannot be edited or deleted at
/// all, and <see cref="RoleKind.Everyone"/> can have its permissions changed but not its name, and
/// cannot be deleted or handed out. A server whose administrative role could be stripped of its
/// permissions is a server nobody can administer again.
/// </item>
/// <item>
/// Nobody may hand out what they do not hold. Every write is checked against the caller's own
/// permissions, so <see cref="Permission.ManageRoles"/> lets somebody delegate their own authority
/// and no more — it is not a route to <see cref="Permission.Administrator"/>. Holding
/// <see cref="Permission.Administrator"/> satisfies this trivially, which is the point of it.
/// </item>
/// </list>
/// </remarks>
public sealed class RoleService
{
    /// <summary>
    /// Where the owner role sits, well clear of anything made by hand so it sorts to the top of the
    /// list. Nothing enforces it: <see cref="RoleRecord.Position"/> is presentation only, because
    /// permissions are the union of a member's roles and no role outranks another.
    /// </summary>
    private const int OwnerPosition = 1000;

    private readonly IRoleStore _store;
    private readonly IUserStore _users;
    private readonly IPermissionResolver _permissions;
    private readonly RolePermissionCache _cache;
    private readonly RoleOptions _options;
    private readonly IEventPublisher _events;
    private readonly TimeProvider _time;

    public RoleService(
        IRoleStore store,
        IUserStore users,
        IPermissionResolver permissions,
        RolePermissionCache cache,
        RoleOptions options,
        IEventPublisher events,
        TimeProvider? time = null)
    {
        _store = store;
        _users = users;
        _permissions = permissions;
        _cache = cache;
        _options = options;
        _events = events;
        _time = time ?? TimeProvider.System;
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    // ── Reading ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Every role on this server. Readable by any member: a role list is not a secret, and a client
    /// needs it to show who is a moderator without asking permission first.
    /// </summary>
    public async Task<RoleListResponse> ListAsync(UserSession session, CancellationToken cancellationToken)
    {
        await RequireMemberAsync(session.UserId, cancellationToken);

        var roles = await _store.ListAsync(cancellationToken);
        return new RoleListResponse(roles.Select(ToSummary).ToList());
    }

    /// <summary>One role and the UUIDs of the members holding it.</summary>
    public async Task<RoleDetail> GetAsync(
        UserSession session, RoleRequest request, CancellationToken cancellationToken)
    {
        await RequireMemberAsync(session.UserId, cancellationToken);

        var role = await RequireRoleAsync(request.RoleId, cancellationToken);
        var members = role.Kind == RoleKind.Everyone
            ? []
            : await _store.ListMembersAsync(role.RoleId, cancellationToken);

        return new RoleDetail(ToSummary(role), members);
    }

    /// <summary>The permissions this build understands, for a client building a role editor.</summary>
    public static PermissionCatalogueResponse Catalogue() => new(PermissionNames.Describe);

    /// <summary>What one member's roles come to. Any member may ask about any other.</summary>
    public async Task<MemberRolesResponse> GetMemberRolesAsync(
        UserSession session, Guid userId, CancellationToken cancellationToken)
    {
        await RequireMemberAsync(session.UserId, cancellationToken);

        if (userId == Guid.Empty)
            throw ApiException.BadRequest("invalid_user_id", "A non-empty UUID is required.");

        return await DescribeAsync(userId, cancellationToken);
    }

    // ── The lifecycle ────────────────────────────────────────────────────────

    /// <summary>Creates a role. Needs <see cref="Permission.ManageRoles"/> and every permission it grants.</summary>
    public async Task<RoleSummary> CreateAsync(
        UserSession session, CreateRoleRequest request, CancellationToken cancellationToken)
    {
        var caller = await RequireManageRolesAsync(session.UserId, cancellationToken);

        var name = RoleNamePolicy.Validate(request.Name);
        var permissions = PermissionNames.Parse(request.Permissions);
        RequireGrantable(caller, permissions, "create a role granting");

        var normalized = RoleNamePolicy.Normalize(name);
        await RequireNameFreeAsync(normalized, cancellationToken);

        var role = new RoleRecord
        {
            RoleId = Guid.NewGuid(),
            Name = name,
            NameNormalized = normalized,
            Permissions = permissions,
            Position = request.Position ?? await NextPositionAsync(cancellationToken),
            Kind = RoleKind.Custom,
            CreatedAtUtc = UtcNow
        };

        await _store.InsertAsync(role, cancellationToken);

        // Nobody holds it yet, so no cached answer can have changed.
        var summary = ToSummary(role);
        await _events.PublishAsync(ServerEventTypes.RoleCreated, summary);

        return summary;
    }

    /// <summary>
    /// Edits a role. Absent fields are left alone, so renaming cannot accidentally clear a permission
    /// set; an empty permission list, on the other hand, clears it deliberately.
    /// </summary>
    public async Task<RoleSummary> UpdateAsync(
        UserSession session, UpdateRoleRequest request, CancellationToken cancellationToken)
    {
        var caller = await RequireManageRolesAsync(session.UserId, cancellationToken);
        var role = await RequireRoleAsync(request.RoleId, cancellationToken);

        if (role.Kind == RoleKind.Owner)
            throw ApiException.Forbidden("role_immutable",
                "The owner role cannot be edited. It is what keeps this server administrable.");

        // Editing a role you could not have created is the same escalation by another route, so the
        // set it already grants is checked alongside the set it is being given.
        var permissions = request.Permissions is null ? role.Permissions : PermissionNames.Parse(request.Permissions);
        RequireGrantable(caller, role.Permissions | permissions, "change a role granting");

        var name = role.Name;
        var normalized = role.NameNormalized;

        if (request.Name is not null)
        {
            if (role.Kind == RoleKind.Everyone)
                throw ApiException.Forbidden("role_immutable",
                    "The everyone role cannot be renamed; its permissions are what you change.");

            name = RoleNamePolicy.Validate(request.Name);
            normalized = RoleNamePolicy.Normalize(name);

            // Re-casing the name it already has is not a claim on a new one.
            if (!string.Equals(normalized, role.NameNormalized, StringComparison.Ordinal))
                await RequireNameFreeAsync(normalized, cancellationToken);
        }

        var updated = role with
        {
            Name = name,
            NameNormalized = normalized,
            Permissions = permissions,
            Position = request.Position ?? role.Position
        };

        await _store.UpdateAsync(updated, cancellationToken);
        _cache.Clear();

        var summary = ToSummary(updated);
        await _events.PublishAsync(ServerEventTypes.RoleUpdated, summary);

        return summary;
    }

    /// <summary>Deletes a role and takes it away from everyone holding it. Built-in roles are refused.</summary>
    public async Task<RoleDeletedResponse> DeleteAsync(
        UserSession session, RoleRequest request, CancellationToken cancellationToken)
    {
        var caller = await RequireManageRolesAsync(session.UserId, cancellationToken);
        var role = await RequireRoleAsync(request.RoleId, cancellationToken);

        if (role.IsBuiltIn)
            throw ApiException.Forbidden("role_immutable",
                $"The {RoleKindNames.Of(role.Kind)} role is built in and cannot be deleted.");

        RequireGrantable(caller, role.Permissions, "delete a role granting");

        await _store.DeleteAsync(role.RoleId, cancellationToken);
        _cache.Clear();

        await _events.PublishAsync(ServerEventTypes.RoleDeleted, new RoleDeletedEvent(role.RoleId));

        return new RoleDeletedResponse(role.RoleId,
            $"The role '{role.Name}' has been deleted and taken away from everyone who held it.");
    }

    // ── Handing roles out ────────────────────────────────────────────────────

    /// <summary>
    /// Grants a role to a member. Needs <see cref="Permission.ManageRoles"/> and everything the role
    /// grants — which is what makes handing out the owner role something only an owner can do.
    /// </summary>
    public async Task<MemberRoleChangedResponse> AssignAsync(
        UserSession session, MemberRoleRequest request, CancellationToken cancellationToken)
    {
        var (caller, role, member) = await ResolveAssignmentAsync(session, request, cancellationToken);
        RequireGrantable(caller, role.Permissions, "hand out a role granting");

        var assigned = await _store.AssignAsync(
            new RoleAssignment(member.UserId, role.RoleId, UtcNow, session.UserId), cancellationToken);

        await RoleChangedAsync(assigned, member.UserId, role.RoleId, true);

        return new MemberRoleChangedResponse(member.UserId, role.RoleId, assigned,
            assigned
                ? $"{member.Username} now has the '{role.Name}' role."
                : $"{member.Username} already had the '{role.Name}' role.");
    }

    /// <summary>
    /// Takes a role away. The last member holding the owner role keeps it: a server with no owner is
    /// one nobody can ever configure again, and there is no account above them to put it right.
    /// </summary>
    public async Task<MemberRoleChangedResponse> UnassignAsync(
        UserSession session, MemberRoleRequest request, CancellationToken cancellationToken)
    {
        var (caller, role, member) = await ResolveAssignmentAsync(session, request, cancellationToken);
        RequireGrantable(caller, role.Permissions, "take away a role granting");

        if (role.Kind == RoleKind.Owner)
            await RequireAnotherOwnerAsync(role, member.UserId, cancellationToken);

        var removed = await _store.UnassignAsync(member.UserId, role.RoleId, cancellationToken);

        await RoleChangedAsync(removed, member.UserId, role.RoleId, false);

        return new MemberRoleChangedResponse(member.UserId, role.RoleId, removed,
            removed
                ? $"{member.Username} no longer has the '{role.Name}' role."
                : $"{member.Username} did not have the '{role.Name}' role.");
    }

    // ── For the server owner's console ───────────────────────────────────────
    // These bypass every permission check by design: whoever is at the server's console owns the
    // machine, the database and the process, so there is nothing left for a check to protect. They
    // are the recovery path for a server whose owner left, and the only way to grant the first role
    // on a server where FirstMemberBecomesOwner is off.

    /// <summary>Every role on this server, for the console to print. No permission check.</summary>
    public Task<IReadOnlyList<RoleRecord>> ListForConsoleAsync(CancellationToken cancellationToken) =>
        _store.ListAsync(cancellationToken);

    /// <summary>Grants a role by name to a member by username, from the console. No permission check.</summary>
    /// <returns>A line to print to the server owner.</returns>
    public async Task<string> GrantFromConsoleAsync(
        string username, string roleName, CancellationToken cancellationToken)
    {
        var (member, role) = await ResolveByNameAsync(username, roleName, cancellationToken);

        if (role.Kind == RoleKind.Everyone)
            return "Every member already has the everyone role; it is not handed out.";

        var assigned = await _store.AssignAsync(
            new RoleAssignment(member.UserId, role.RoleId, UtcNow, null), cancellationToken);

        await RoleChangedAsync(assigned, member.UserId, role.RoleId, true);

        return assigned
            ? $"{member.Username} now has the '{role.Name}' role."
            : $"{member.Username} already had the '{role.Name}' role.";
    }

    /// <summary>Takes a role away, from the console. No permission check.</summary>
    /// <returns>A line to print to the server owner.</returns>
    public async Task<string> RevokeFromConsoleAsync(
        string username, string roleName, CancellationToken cancellationToken)
    {
        var (member, role) = await ResolveByNameAsync(username, roleName, cancellationToken);

        if (role.Kind == RoleKind.Everyone)
            return "The everyone role cannot be taken away; it is what being a member means.";

        var removed = await _store.UnassignAsync(member.UserId, role.RoleId, cancellationToken);

        await RoleChangedAsync(removed, member.UserId, role.RoleId, false);

        return removed
            ? $"{member.Username} no longer has the '{role.Name}' role."
            : $"{member.Username} did not have the '{role.Name}' role.";
    }

    // ── Bootstrapping ────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the two built-in roles if this server has never had them. Runs at startup, before the
    /// first request, so that the owner role exists by the time the first UUID registers.
    /// </summary>
    /// <remarks>
    /// Static, and taking the store rather than reading a field, because it runs before there is a
    /// server to have services: <see cref="ServiceCollectionExtensions.InitializeRaindropDatabaseAsync"/>
    /// calls it with the store the container is about to hand out, alongside creating the schema.
    /// </remarks>
    public static async Task EnsureBuiltInRolesAsync(
        IRoleStore store, RoleOptions options, TimeProvider? time, CancellationToken cancellationToken)
    {
        var now = (time ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var everyone = await store.FindByKindAsync(RoleKind.Everyone, cancellationToken);
        var freshServer = everyone is null;

        if (everyone is null)
        {
            await store.InsertAsync(new RoleRecord
            {
                RoleId = Guid.NewGuid(),
                Name = "everyone",
                NameNormalized = "everyone",
                Permissions = options.DefaultMemberPermissions,
                Position = 0,
                Kind = RoleKind.Everyone,
                CreatedAtUtc = now
            }, cancellationToken);
        }

        if (await store.FindByKindAsync(RoleKind.Owner, cancellationToken) is null)
        {
            await store.InsertAsync(new RoleRecord
            {
                RoleId = Guid.NewGuid(),
                Name = "owner",
                NameNormalized = "owner",
                Permissions = Permission.Administrator,
                Position = OwnerPosition,
                Kind = RoleKind.Owner,
                CreatedAtUtc = now
            }, cancellationToken);
        }

        await ApplyPermissionRolloutsAsync(store, everyone, now, cancellationToken);
    }

    /// <summary>
    /// Hands the <c>everyone</c> role each <see cref="PermissionRollout"/> it has not had yet — see
    /// <see cref="PermissionRollouts"/> for why. <paramref name="existingEveryone"/> is null on a
    /// server whose <c>everyone</c> role was only just seeded: that seed already reflects this build,
    /// so every rollout is recorded without granting anything.
    /// </summary>
    /// <remarks>
    /// Grant first, record second, so a crash between the two re-applies an OR that changes nothing
    /// rather than recording a grant that never happened. A store written before rollouts existed
    /// throws <see cref="NotSupportedException"/>, and the server starts without them rather than not
    /// at all.
    /// </remarks>
    private static async Task ApplyPermissionRolloutsAsync(
        IRoleStore store, RoleRecord? existingEveryone, DateTime now, CancellationToken cancellationToken)
    {
        IReadOnlySet<string> applied;
        try
        {
            applied = await store.ListAppliedRolloutsAsync(cancellationToken);
        }
        catch (NotSupportedException)
        {
            return;
        }

        var everyone = existingEveryone;

        foreach (var rollout in PermissionRollouts.All)
        {
            if (applied.Contains(rollout.Id))
                continue;

            if (everyone is not null && (everyone.Permissions & rollout.GrantToEveryone) != rollout.GrantToEveryone)
            {
                everyone = everyone with { Permissions = everyone.Permissions | rollout.GrantToEveryone };
                await store.UpdateAsync(everyone, cancellationToken);
            }

            await store.MarkRolloutAppliedAsync(rollout.Id, now, cancellationToken);
        }
    }

    /// <summary>
    /// Grants the owner role to <paramref name="userId"/> if nobody holds it yet — how the first UUID
    /// to register ends up administering the server. Does nothing when there is already an owner, or
    /// when the server owner has turned <see cref="RoleOptions.FirstMemberBecomesOwner"/> off.
    /// </summary>
    /// <returns>Whether ownership was claimed.</returns>
    public async Task<bool> TryClaimOwnershipAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!_options.FirstMemberBecomesOwner)
            return false;

        var owner = await _store.FindByKindAsync(RoleKind.Owner, cancellationToken);

        if (owner is null)
            return false;

        var owners = await _store.ListMembersAsync(owner.RoleId, cancellationToken);

        if (owners.Count > 0)
            return false;

        await _store.AssignAsync(new RoleAssignment(userId, owner.RoleId, UtcNow, null), cancellationToken);
        await RoleChangedAsync(true, userId, owner.RoleId, true);

        return true;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// After an assignment or removal: clears the permission cache and tells everyone, but only when
    /// something actually changed — handing somebody a role they already held is not news.
    /// </summary>
    private async Task RoleChangedAsync(bool changed, Guid userId, Guid roleId, bool assigned)
    {
        if (!changed)
            return;

        _cache.Clear();
        await _events.PublishAsync(ServerEventTypes.MemberRoles, new MemberRolesEvent(userId, roleId, assigned));
    }

    /// <summary>Turns a role into the shape sent over the wire.</summary>
    public static RoleSummary ToSummary(RoleRecord role) => new(
        role.RoleId,
        role.Name,
        RoleKindNames.Of(role.Kind),
        role.Position,
        PermissionNames.Of(role.Permissions),
        role.CreatedAtUtc);

    /// <summary>
    /// A member's roles and what they add up to. The everyone role is listed even though nobody is
    /// assigned it, because leaving it out would make the permission list look unexplained.
    /// </summary>
    private async Task<MemberRolesResponse> DescribeAsync(Guid userId, CancellationToken cancellationToken)
    {
        var held = await _store.ListForUserAsync(userId, cancellationToken);
        var everyone = await _store.FindByKindAsync(RoleKind.Everyone, cancellationToken);

        var roles = everyone is null ? held : held.Prepend(everyone);
        var permissions = await _permissions.GetAsync(userId, null, cancellationToken);

        return new MemberRolesResponse(
            userId,
            roles.OrderByDescending(role => role.Position)
                 .ThenBy(role => role.NameNormalized, StringComparer.Ordinal)
                 .Select(ToSummary)
                 .ToList(),
            PermissionNames.Of(permissions));
    }

    /// <summary>
    /// Everything an assign or unassign needs: the caller's permissions, the role, and the member —
    /// each of which has to exist and be a member before the operation is worth checking.
    /// </summary>
    private async Task<(Permission Caller, RoleRecord Role, UserRecord Member)> ResolveAssignmentAsync(
        UserSession session, MemberRoleRequest request, CancellationToken cancellationToken)
    {
        var caller = await RequireManageRolesAsync(session.UserId, cancellationToken);
        var role = await RequireRoleAsync(request.RoleId, cancellationToken);

        if (role.Kind == RoleKind.Everyone)
            throw ApiException.BadRequest("role_not_assignable",
                "The everyone role is not handed out: every active member has it already.");

        var member = await _users.FindByIdAsync(request.UserId, cancellationToken);

        if (member is null || member.Status != UserStatus.Active)
            throw ApiException.NotFound("member_not_found",
                $"There is no active member {request.UserId} on this server.");

        return (caller, role, member);
    }

    /// <summary>Resolves a username and a role name for the console, reporting either miss plainly.</summary>
    private async Task<(UserRecord Member, RoleRecord Role)> ResolveByNameAsync(
        string username, string roleName, CancellationToken cancellationToken)
    {
        var member = await _users.FindByUsernameAsync(UsernamePolicy.Normalize(username ?? string.Empty), cancellationToken);

        if (member is null || member.Status != UserStatus.Active)
            throw new InvalidOperationException($"There is no active member called '{username}' on this server.");

        var role = await _store.FindByNameAsync(RoleNamePolicy.Normalize(roleName ?? string.Empty), cancellationToken);

        if (role is null)
            throw new InvalidOperationException($"There is no role called '{roleName}' on this server.");

        return (member, role);
    }

    /// <summary>
    /// Whether <paramref name="userId"/> is the only member holding the owner role — the one whose
    /// departure would leave the server with nobody able to administer it.
    /// </summary>
    public async Task<bool> IsSoleOwnerAsync(Guid userId, CancellationToken cancellationToken)
    {
        var owner = await _store.FindByKindAsync(RoleKind.Owner, cancellationToken);

        return owner is not null && await IsSoleHolderAsync(owner, userId, cancellationToken);
    }

    private async Task<bool> IsSoleHolderAsync(RoleRecord role, Guid userId, CancellationToken cancellationToken)
    {
        var holders = await _store.ListMembersAsync(role.RoleId, cancellationToken);

        return holders.Count <= 1 && holders.Contains(userId);
    }

    /// <summary>Refuses to remove the only owner, which would leave the server with nobody able to administer it.</summary>
    private async Task RequireAnotherOwnerAsync(RoleRecord owner, Guid leaving, CancellationToken cancellationToken)
    {
        if (await IsSoleHolderAsync(owner, leaving, cancellationToken))
            throw ApiException.Conflict("last_owner",
                "This is the only member with the owner role. Give it to somebody else first.");
    }

    /// <summary>Every permission set a write touches has to be one the caller already holds.</summary>
    private static void RequireGrantable(Permission caller, Permission target, string action)
    {
        var missing = target.Expand() & ~caller.Expand();

        if (missing == Permission.None)
            return;

        throw ApiException.Forbidden("missing_permission",
            $"You do not have permission to {action} {string.Join(", ", PermissionNames.Of(missing))}.");
    }

    /// <summary>
    /// Checks membership and <see cref="Permission.ManageRoles"/> separately, rather than reading
    /// <see cref="Permission.None"/> as "not a member". On a server whose everyone role grants
    /// nothing, an ordinary member holds nothing either, and telling them their session had expired
    /// would send a client off to log in again over a permission it will never be given.
    /// </summary>
    private async Task<Permission> RequireManageRolesAsync(Guid userId, CancellationToken cancellationToken)
    {
        await RequireMemberAsync(userId, cancellationToken);

        var permissions = await _permissions.GetAsync(userId, null, cancellationToken);
        permissions.Require(Permission.ManageRoles, "manage roles on this server");

        return permissions;
    }

    private async Task RequireMemberAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await _users.FindByIdAsync(userId, cancellationToken);

        if (user is null || user.Status != UserStatus.Active)
            throw ApiException.Unauthorized("invalid_session", "This account is no longer a member of the server.");
    }

    private async Task<RoleRecord> RequireRoleAsync(Guid roleId, CancellationToken cancellationToken)
    {
        var role = roleId == Guid.Empty ? null : await _store.FindByIdAsync(roleId, cancellationToken);

        return role ?? throw ApiException.NotFound("role_not_found", $"There is no role {roleId} on this server.");
    }

    private async Task RequireNameFreeAsync(string normalized, CancellationToken cancellationToken)
    {
        if (await _store.FindByNameAsync(normalized, cancellationToken) is not null)
            throw ApiException.Conflict("role_name_taken", "A role with that name already exists on this server.");
    }

    private async Task<int> NextPositionAsync(CancellationToken cancellationToken)
    {
        var roles = await _store.ListAsync(cancellationToken);

        return roles.Where(role => role.Kind == RoleKind.Custom)
                    .Select(role => role.Position)
                    .DefaultIfEmpty(0)
                    .Max() + 1;
    }
}
