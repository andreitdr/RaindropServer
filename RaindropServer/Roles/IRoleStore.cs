namespace RaindropServer.Roles;

/// <summary>
/// Persistence for roles and for who holds them. Implemented once per database module — see
/// <see cref="Data.IDatabaseDriver"/> — like <see cref="Users.IUserStore"/> and
/// <see cref="Channels.IChannelStore"/>.
/// </summary>
public interface IRoleStore
{
    // ── The roles themselves ─────────────────────────────────────────────────

    /// <summary>Finds a role by its id, or null when no such role exists here.</summary>
    Task<RoleRecord?> FindByIdAsync(Guid roleId, CancellationToken cancellationToken);

    /// <summary>Finds a role by its normalized (lower-cased) name, or null when the name is unclaimed.</summary>
    Task<RoleRecord?> FindByNameAsync(string nameNormalized, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the one role of a built-in kind, or null before it has been seeded. Meaningless for
    /// <see cref="RoleKind.Custom"/>, of which there may be any number.
    /// </summary>
    Task<RoleRecord?> FindByKindAsync(RoleKind kind, CancellationToken cancellationToken);

    /// <summary>Every role on this server, highest <see cref="RoleRecord.Position"/> first, then by name.</summary>
    Task<IReadOnlyList<RoleRecord>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Inserts a brand-new role row.</summary>
    Task InsertAsync(RoleRecord role, CancellationToken cancellationToken);

    /// <summary>Overwrites every mutable column of an existing row, matched on <see cref="RoleRecord.RoleId"/>.</summary>
    Task UpdateAsync(RoleRecord role, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the role and every assignment of it. The two go together: an assignment pointing at a
    /// role that is gone would grant nothing but would still be listed against a member.
    /// </summary>
    Task DeleteAsync(Guid roleId, CancellationToken cancellationToken);

    // ── Who holds them ───────────────────────────────────────────────────────

    /// <summary>
    /// The roles one member has been given, in the same order <see cref="ListAsync"/> uses. Does not
    /// include <see cref="RoleKind.Everyone"/>, which nobody is assigned.
    /// </summary>
    Task<IReadOnlyList<RoleRecord>> ListForUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>The UUIDs of every member holding a role, in the order they were granted it.</summary>
    Task<IReadOnlyList<Guid>> ListMembersAsync(Guid roleId, CancellationToken cancellationToken);

    /// <summary>
    /// Grants a role to a member, doing nothing if they already hold it. Returns whether the
    /// assignment was new, so the caller can report "already had it" rather than a silent success.
    /// </summary>
    Task<bool> AssignAsync(RoleAssignment assignment, CancellationToken cancellationToken);

    /// <summary>Takes a role away from a member. Returns whether they held it in the first place.</summary>
    Task<bool> UnassignAsync(Guid userId, Guid roleId, CancellationToken cancellationToken);

    // ── One-time permission rollouts ─────────────────────────────────────────

    /// <summary>
    /// The ids of every <see cref="PermissionRollout"/> this database has already had applied. The
    /// default throws, so a store written before rollouts existed still compiles — seeding reports it
    /// and carries on without them.
    /// </summary>
    Task<IReadOnlySet<string>> ListAppliedRolloutsAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{GetType().Name} does not record permission rollouts.");

    /// <summary>
    /// Records that a rollout has been applied, doing nothing if it already was. See
    /// <see cref="ListAppliedRolloutsAsync"/> for why there is a default.
    /// </summary>
    Task MarkRolloutAppliedAsync(string rolloutId, DateTime appliedAtUtc, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{GetType().Name} does not record permission rollouts.");
}
