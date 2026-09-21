namespace RaindropServer.Users;

/// <summary>
/// A user's membership of this server, as persisted by an <see cref="IUserStore"/>.
/// The <see cref="UserId"/> is the client-generated GUID and is unique to this server only — the same
/// client uses a different GUID on every other server it joins.
/// </summary>
public sealed record UserRecord
{
    /// <summary>The client-generated GUID identifying this user on this server.</summary>
    public required Guid UserId { get; init; }

    /// <summary>The username as the user typed it. Null once the account has been erased.</summary>
    public string? Username { get; init; }

    /// <summary>The lower-cased username that the uniqueness constraint is applied to. Null once erased.</summary>
    public string? UsernameNormalized { get; init; }

    /// <summary>The encoded password hash (see <see cref="Pbkdf2PasswordHasher"/>). Null once erased.</summary>
    public string? PasswordHash { get; init; }

    /// <summary>Where this user sits in the membership lifecycle.</summary>
    public required UserStatus Status { get; init; }

    /// <summary>When the UUID first registered on this server.</summary>
    public required DateTime CreatedAtUtc { get; init; }

    /// <summary>When the user last logged in successfully, if ever.</summary>
    public DateTime? LastLoginAtUtc { get; init; }

    /// <summary>When the user left the server, if they have.</summary>
    public DateTime? LeftAtUtc { get; init; }

    /// <summary>
    /// When preserved data must be erased. Set only for <see cref="UserStatus.LeftDataPreserved"/>,
    /// and capped by <see cref="UserOptions.DataRetentionAfterLeaving"/> (two years by default).
    /// </summary>
    public DateTime? DataExpiresAtUtc { get; init; }
}

/// <summary>Helpers for moving a <see cref="UserRecord"/> through its lifecycle.</summary>
public static class UserRecordExtensions
{
    /// <summary>
    /// Strips every piece of personal data from the record, keeping only the UUID tombstone so that
    /// messages already linked to that UUID stay readable. Used both when a user leaves asking for
    /// deletion and when a preserved account's retention window closes.
    /// </summary>
    public static UserRecord AsErased(this UserRecord user, DateTime utcNow) => user with
    {
        Username = null,
        UsernameNormalized = null,
        PasswordHash = null,
        Status = UserStatus.Erased,
        LastLoginAtUtc = null,
        LeftAtUtc = user.LeftAtUtc ?? utcNow,
        DataExpiresAtUtc = null
    };
}
