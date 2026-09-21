namespace RaindropServer.Users;

/// <summary>
/// Persistence for user records. Implemented once per database driver module — see
/// <see cref="Data.IDatabaseDriver"/> — so a server owner can run Raindrop on whichever
/// database they already have.
/// </summary>
public interface IUserStore
{
    /// <summary>Finds a user by their UUID, or null when the UUID is unknown to this server.</summary>
    Task<UserRecord?> FindByIdAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Finds a user by their normalized (lower-cased) username, or null when it is unclaimed.</summary>
    Task<UserRecord?> FindByUsernameAsync(string usernameNormalized, CancellationToken cancellationToken);

    /// <summary>Whether the normalized username is already claimed on this server.</summary>
    Task<bool> UsernameExistsAsync(string usernameNormalized, CancellationToken cancellationToken);

    /// <summary>Inserts a brand-new user row.</summary>
    Task InsertAsync(UserRecord user, CancellationToken cancellationToken);

    /// <summary>Overwrites every mutable column of an existing row, matched on <see cref="UserRecord.UserId"/>.</summary>
    Task UpdateAsync(UserRecord user, CancellationToken cancellationToken);

    /// <summary>
    /// Every user in one lifecycle state, ordered by normalized username. Asking for
    /// <see cref="UserStatus.Active"/> is what produces the server's roster.
    /// </summary>
    /// <remarks>
    /// Unpaged: a self-hosted server's membership is small enough to send at once, and a roster
    /// sorted by presence cannot be paged by the database anyway, since presence is not a column.
    /// A server large enough to need paging needs presence to live somewhere queryable first.
    /// </remarks>
    Task<IReadOnlyList<UserRecord>> ListByStatusAsync(UserStatus status, CancellationToken cancellationToken);

    /// <summary>
    /// Returns users who left with their data preserved and whose retention window has now closed,
    /// so that <see cref="RetentionCleanupService"/> can erase them.
    /// </summary>
    Task<IReadOnlyList<UserRecord>> FindExpiredPreservedAsync(DateTime utcNow, CancellationToken cancellationToken);
}
