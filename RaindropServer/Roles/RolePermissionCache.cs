using System.Collections.Concurrent;

namespace RaindropServer.Roles;

/// <summary>
/// Remembers what each member's roles add up to, so that drawing a channel list does not cost one
/// role query per channel. Held for the life of the process and dropped whenever roles change.
/// </summary>
/// <remarks>
/// The invalidation is deliberately blunt — any write through <see cref="RoleService"/> clears the
/// whole thing — because role changes are rare and a stale permission is a security bug, while a
/// cold cache costs one query. This is correct only because a Raindrop server is a single process:
/// like <see cref="Users.InMemorySessionStore"/>, it assumes nothing else writes to the database
/// behind its back. A role granted directly in SQL takes effect on the next restart.
/// </remarks>
public sealed class RolePermissionCache
{
    private readonly ConcurrentDictionary<Guid, Permission> _byUser = new();

    /// <summary>Reads a member's cached server-wide permissions, if they have been worked out since the last change.</summary>
    public bool TryGet(Guid userId, out Permission permissions) => _byUser.TryGetValue(userId, out permissions);

    /// <summary>Remembers what a member's roles add up to.</summary>
    public void Set(Guid userId, Permission permissions) => _byUser[userId] = permissions;

    /// <summary>Forgets everything, so the next check re-reads the roles from the database.</summary>
    public void Clear() => _byUser.Clear();
}
