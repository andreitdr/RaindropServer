using RaindropServer.Users;

namespace RaindropServer.Members;

/// <summary>
/// The server's roster: who is a member, and where each of them is right now. Every rule lives here,
/// so the service serving it stays thin and the database driver stays dumb.
/// </summary>
/// <remarks>
/// There is no membership table — a member <em>is</em> an active user
/// (<see cref="UserStatus.Active"/>), because a Raindrop server is a single place rather than a
/// directory of many. So this reads the user store and asks an <see cref="IPresenceTracker"/> for
/// the half that is not persisted.
/// </remarks>
public sealed class MemberService
{
    private readonly IUserStore _users;
    private readonly IPresenceTracker _presence;

    public MemberService(IUserStore users, IPresenceTracker presence)
    {
        _users = users;
        _presence = presence;
    }

    /// <summary>
    /// Every active member, ordered the way a roster is drawn: by presence in the order
    /// <see cref="PresenceStatus"/> declares, then alphabetically by username within each.
    /// </summary>
    /// <remarks>
    /// Sorted here rather than left to the client because the ordering is part of what the roster
    /// means, and every client would otherwise re-derive it. A client still groups the list under
    /// headings itself — the presence of any member can change between one request and the next.
    /// </remarks>
    public async Task<MemberListResponse> ListAsync(UserSession session, CancellationToken cancellationToken)
    {
        // Sessions outlive the account behind them, so membership is re-checked rather than trusted
        // from the token — the same rule the channel permission resolver applies.
        var caller = await _users.FindByIdAsync(session.UserId, cancellationToken);

        if (caller is null || caller.Status != UserStatus.Active)
            throw ApiException.Unauthorized("invalid_session", "This account is no longer a member of the server.");

        var members = await _users.ListByStatusAsync(UserStatus.Active, cancellationToken);
        var presence = await _presence.GetAsync(
            members.Select(m => m.UserId).ToList(), cancellationToken);

        var roster = members
            .Select(user => (user, presence: Presence(presence, user.UserId)))
            .OrderBy(entry => entry.presence)
            .ThenBy(entry => entry.user.Username, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new Member(
                entry.user.UserId,
                entry.user.Username ?? string.Empty,
                PresenceStatusNames.Of(entry.presence)))
            .ToList();

        return new MemberListResponse(roster);
    }

    /// <summary>
    /// An id a tracker said nothing about counts as offline. A presence tracker not knowing where
    /// somebody is is exactly what being offline means, so this is the answer rather than an error.
    /// </summary>
    private static PresenceStatus Presence(IReadOnlyDictionary<Guid, PresenceStatus> statuses, Guid userId) =>
        statuses.TryGetValue(userId, out var status) ? status : PresenceStatus.Offline;
}
