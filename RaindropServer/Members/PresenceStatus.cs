namespace RaindropServer.Members;

/// <summary>
/// Whether a member is around right now — as opposed to
/// <see cref="Users.UserStatus"/>, which is whether they are a member at all. The two are
/// independent: an active member is offline most of the time.
/// </summary>
/// <remarks>
/// Declared in the order a roster shows them, online first, and sorted on that order rather than on
/// a separate ranking. That is safe because presence is <em>derived</em> — computed from live
/// sessions on every request and never written to the database — so a new status can be inserted in
/// its rightful place without renumbering anything that already exists and without a migration.
/// Clients sort by the same declared order, reading the names below rather than the numbers.
/// </remarks>
public enum PresenceStatus
{
    /// <summary>The member has at least one session that has not lapsed.</summary>
    Online = 0,

    /// <summary>
    /// Nobody is logged in as this member. Also where a member lands when the server restarts, since
    /// sessions are not persisted.
    /// </summary>
    Offline = 1

    // Idle, DoNotDisturb and Invisible belong between these two when there is something to drive
    // them: idle needs a last-activity timestamp per session, and the other two need the member to
    // be able to say so, which is a stored preference rather than something the server can observe.
}

/// <summary>The wire names for <see cref="PresenceStatus"/>, which is how a client sees presence.</summary>
public static class PresenceStatusNames
{
    public const string Online = "online";
    public const string Offline = "offline";

    public static string Of(PresenceStatus status) => status switch
    {
        PresenceStatus.Online => Online,
        PresenceStatus.Offline => Offline,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown presence status.")
    };
}
