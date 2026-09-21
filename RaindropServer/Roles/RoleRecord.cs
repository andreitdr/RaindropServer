namespace RaindropServer.Roles;

/// <summary>
/// A role on this server, as persisted by an <see cref="IRoleStore"/>. A role is a named set of
/// permissions; a member holds any number of them and may do whatever the union allows.
/// </summary>
public sealed record RoleRecord
{
    /// <summary>The server-generated id for this role.</summary>
    public required Guid RoleId { get; init; }

    /// <summary>The role name as it was typed, e.g. "Moderator".</summary>
    public required string Name { get; init; }

    /// <summary>The lower-cased name the uniqueness constraint is applied to.</summary>
    public required string NameNormalized { get; init; }

    /// <summary>What holding this role allows.</summary>
    public required Permission Permissions { get; init; }

    /// <summary>
    /// Where the role sits in the list, highest first. Presentation only: permissions are the union
    /// of every role a member holds, so nothing here outranks anything else.
    /// </summary>
    public required int Position { get; init; }

    /// <summary>Whether the server defines this role itself, and so what may be done to it.</summary>
    public required RoleKind Kind { get; init; }

    /// <summary>When the role was created.</summary>
    public required DateTime CreatedAtUtc { get; init; }

    /// <summary>Whether this role is one of the two the server defines for itself.</summary>
    public bool IsBuiltIn => Kind != RoleKind.Custom;
}

/// <summary>
/// One member holding one role. Assignments are keyed to the member's UUID, like everything else
/// that outlives an account, so a member who leaves with their data preserved keeps their roles when
/// they rejoin. They grant nothing while the account is not active — the resolver checks membership
/// before it looks at a single role.
/// </summary>
/// <param name="UserId">The UUID of the member holding the role.</param>
/// <param name="RoleId">The role they hold.</param>
/// <param name="AssignedAtUtc">When it was granted.</param>
/// <param name="AssignedBy">The UUID of whoever granted it, or null when the server did.</param>
public sealed record RoleAssignment(Guid UserId, Guid RoleId, DateTime AssignedAtUtc, Guid? AssignedBy);
