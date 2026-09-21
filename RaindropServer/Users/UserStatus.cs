namespace RaindropServer.Users;

/// <summary>The lifecycle state of a user's membership on this server.</summary>
public enum UserStatus
{
    /// <summary>A member of the server, able to log in.</summary>
    Active = 0,

    /// <summary>
    /// The user left the server but opted in to having their data preserved so they can rejoin later.
    /// Username and password are kept until <see cref="UserRecord.DataExpiresAtUtc"/>, then erased.
    /// </summary>
    LeftDataPreserved = 1,

    /// <summary>
    /// The user left and asked for their data to be deleted. Username and password are gone; only the
    /// UUID remains as a tombstone so that messages — which are linked to the UUID — stay intact.
    /// The same UUID may register again from scratch.
    /// </summary>
    Erased = 2
}
