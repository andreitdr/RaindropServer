namespace RaindropServer.Members;

// ── Shared shapes ────────────────────────────────────────────────────────────

/// <summary>
/// One member of this server, as the roster shows them.
/// </summary>
/// <param name="UserId">
/// The UUID this member is known by here. It is what messages are linked to, so this is also how a
/// client tells which row in the roster wrote a message — and which row is the caller themselves.
/// </param>
/// <param name="Username">The member's current username, unique on this server.</param>
/// <param name="Presence">
/// Where they are right now, as a <see cref="PresenceStatusNames"/> value. Deliberately not called
/// "status": a user's <c>state</c> elsewhere in the API is their membership lifecycle
/// (<c>active</c>, <c>left_preserved</c>, <c>erased</c>), which is a different question entirely.
/// </param>
public record Member(Guid UserId, string Username, string Presence);

// ── Responses ────────────────────────────────────────────────────────────────

/// <param name="Members">
/// Every active member of the server, ordered by presence and then by username — the order a roster
/// is drawn in, so a client can render the list as it arrives. Members who have left are not here at
/// all, whether their data was preserved or erased: they are no longer members.
/// </param>
public record MemberListResponse(IReadOnlyList<Member> Members);
