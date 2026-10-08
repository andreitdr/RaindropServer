# Members

Domain code: [`RaindropServer/Members/`](../RaindropServer/Members/). Wire surface: a single RPC,
`raindrop.v1.MemberService/ListMembers` ([`members.proto`](../../RaindropProto/raindrop/v1/members.proto)),
served by [`MemberGrpcService`](../RaindropServer.Grpc/Services/AccountServices.cs).

## The roster is not a table

There is no membership table — **a member is an active user.** A Raindrop server is one place rather
than a directory of many, so `MemberService` reads the user store (see [Users](users.md)) and asks an
`IPresenceTracker` for the half that is not persisted: presence.

## Presence is derived, never stored

[`PresenceStatus`](../RaindropServer/Members/PresenceStatus.cs) is `Online` or `Offline`.
[`StreamPresenceTracker`](../RaindropServer/Members/StreamPresenceTracker.cs), the default
`IPresenceTracker`, answers it from the event stream registry: **a member is online exactly while they
have an [event stream](events.md) open.**

A signed-in client keeps one stream open per server and sends a heartbeat on it every 20 seconds. The
stream is closed when its session ends, and also when the heartbeats stop for
`EventOptions.HeartbeatTimeout` (60 s). Three consequences follow:

- A client that was killed rather than closed cleanly reads as offline about a minute later.
- A session alone does not count. A client that logged in but has not opened its stream is not
  listening, so it is not "around".
- Everybody reads as offline after a server restart, until their clients reconnect.

The tracker is a seam: a server that wants a different idea of presence (members setting their own
status, say) replaces `IPresenceTracker` and nothing else changes.

### Presence is pushed

[`PresenceBroadcaster`](../RaindropServer/Members/PresenceBroadcaster.cs) is an `IEventStreamListener`
and an `ISessionListener`. It runs when a member's first stream opens or last one closes, and after
every login, logout, password change or departure. Each time, it asks the `IPresenceTracker` for that
member's presence and publishes `member.presence` (see [Events](events.md)). It asks the tracker rather
than reading streams itself, so a server that swaps in a different idea of presence gets events that
agree with its roster. The event carries the state, not a transition, so a second device opening its
stream re-announces `online`. Repeating a state is harmless; missing a transition would not be.

### Why the enum is easy to extend

`PresenceStatus` is deliberately open-ended, and the thing that makes adding to it cheap is that
nothing persists its numeric values. It is **declared in the order a roster shows them**, and sorted
on that declaration order — so adding `Idle` or `DoNotDisturb` means inserting a value between
`Online` and `Offline`, with no renumbering, no migration, and no separate ranking table. On the wire it
is the protobuf enum `raindrop.v1.Presence`; a new status takes the next free number there, whatever
its place in a roster. Clients sort by their own copy of the display order, never by the numbers, and
a value a client is too old to know decodes to `PRESENCE_UNSPECIFIED`, which it shows as offline.

The registry answers presence for the whole roster in one call, `IEventConnectionRegistry.ConnectedUserIds()`,
so building a roster costs one call regardless of how many members it has.

## Paging

The roster is **unpaged**: `IUserStore.ListByStatusAsync` is what it reads, and a list sorted by
presence can't be paged by the database anyway, since presence isn't a stored column. A server large
enough to need paging here needs presence to live somewhere queryable first.

## Naming trap: `presence` is not `state`

A member's `presence` (`online`/`offline`) is not their `state`. `state` (`active`, `left_preserved`,
`erased`) is the membership lifecycle from [Users](users.md); `presence` is only whether they are
around right now, listening on an event stream. **Members who have left do not appear in the roster at all**, regardless of
`presence`.

## Member API

| RPC | Auth | Purpose |
|---|---|---|
| `MemberService.ListMembers` | ✓ | Every active member, with `presence`, ordered by presence then username |

No error codes of its own beyond `invalid_session` (the caller is no longer a valid session holder).

### Domain contracts

Defined in [`RaindropServer/Members/MemberContracts.cs`](../RaindropServer/Members/MemberContracts.cs);
on the wire, `Member` and `ListMembersResponse`:

- `Member(UserId, Username, Presence)` — one roster row.
- `MemberListResponse(Members)` — every active member, ordered by presence then username.

## Related

- [Users](users.md) — the source of who's active and who holds a session.
- [Events](events.md) — `member.joined`, `member.renamed`, `member.left` and `member.presence`.
- The client's [members & presence doc](../../RaindropClient/docs/members-presence.md) — how the
  roster follows these events and is rendered.
