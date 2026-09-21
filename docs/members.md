# Members

Domain code: [`RaindropServer/Members/`](../RaindropServer/Members/). HTTP surface: a single endpoint,
[`ServerCli/Endpoints/Members/ListMembersEndpoint.cs`](../ServerCli/Endpoints/Members/ListMembersEndpoint.cs).

## The roster is not a table

There is no membership table — **a member is an active user.** A Raindrop server is one place rather
than a directory of many, so `MemberService` reads the user store (see [Users](users.md)) and asks an
`IPresenceTracker` for the half that is not persisted: presence.

## Presence is derived, never stored

[`PresenceStatus`](../RaindropServer/Members/PresenceStatus.cs) is `Online` or `Offline`.
[`SessionPresenceTracker`](../RaindropServer/Members/SessionPresenceTracker.cs) — the default
`IPresenceTracker` — answers it from `ISessionStore`: a member is online exactly when they hold a
session that has not lapsed. That's the most the server can honestly know.

Two consequences follow from sessions living only in memory:

- Everybody reads as offline after a server restart.
- A client that was killed rather than closed cleanly reads as online until its idle timeout expires.

Shortening `UserOptions.SessionIdleTimeout` sharpens presence; a client heartbeat would sharpen it
further, and would be implemented behind the same `IPresenceTracker` interface rather than folded into
the session store.

### Presence is pushed

[`PresenceBroadcaster`](../RaindropServer/Members/PresenceBroadcaster.cs) is an `ISessionListener`:
after every login, logout, password change or departure it asks the `IPresenceTracker` for that
member's presence and publishes `member.presence` (see [Events](events.md)). It asks the tracker rather
than reading sessions, so a server that swaps in a different idea of presence gets events that agree
with its roster. The event carries the state, not a transition, so a second device logging in
re-announces `online` — harmless, where a missed transition would not be.

A session that lapses on its own is not announced — it happens lazily inside the store with nobody to
tell. An open event stream renews its session, so this only affects members with no stream open, who
read as online until the idle timeout on the roster too.

### Why the enum is easy to extend

`PresenceStatus` is deliberately open-ended, and the thing that makes adding to it cheap is that
nothing persists its numeric values. It is **declared in the order a roster shows them**, and sorted
on that declaration order — so adding `Idle` or `DoNotDisturb` means inserting a value between
`Online` and `Offline`, with no renumbering, no migration, and no separate ranking table. What travels
on the wire is the status *name* (`online`, `offline`), so a client too old to know a status simply
doesn't render it, rather than crashing on an unrecognized number.

`ISessionStore` gained one method to support this: `ActiveUserIds()` — the whole set of currently
active sessions at once, so building a roster costs one call regardless of how many members it has. It
reports *sessions*, not presence — what a session implies about presence is `Members/`'s business, not
`Users/`'s, which keeps the presence enum out of the user subsystem entirely.

## Paging

The roster is **unpaged**: `IUserStore.ListByStatusAsync` is what it reads, and a list sorted by
presence can't be paged by the database anyway, since presence isn't a stored column. A server large
enough to need paging here needs presence to live somewhere queryable first.

## Naming trap: `presence` is not `state`

A member's `presence` (`online`/`offline`) is not their `state`. `state` (`active`, `left_preserved`,
`erased`) is the membership lifecycle from [Users](users.md); `presence` is only whether they're
currently logged in. **Members who have left do not appear in the roster at all**, regardless of
`presence`.

## Member API

| Method | Route | Auth | Purpose |
|---|---|---|---|
| GET | `/api/members` | ✓ | Every active member, with `presence` (`online`/`offline`), ordered by presence then username |

No error codes of its own beyond `invalid_session` (the caller is no longer a valid session holder).

### Response shape

Defined in [`RaindropServer/Members/MemberContracts.cs`](../RaindropServer/Members/MemberContracts.cs):

- `Member(UserId, Username, Presence)` — one roster row.
- `MemberListResponse(Members)` — every active member, ordered by presence then username.

## Related

- [Users](users.md) — the source of who's active and who holds a session.
- [Events](events.md) — `member.joined`, `member.renamed`, `member.left` and `member.presence`.
- The client's [members & presence doc](../../RaindropClient/docs/members-presence.md) — how the
  roster follows these events and is rendered.
