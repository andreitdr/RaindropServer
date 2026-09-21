# Events (push)

The server pushes changes to connected clients as they happen, over a WebSocket at
`GET /api/events`. A client that has it open no longer needs to poll: a message posted, a channel
renamed, a member coming online or a role handed out reaches everyone allowed to see it within the
same request that caused it.

The code is in [`RaindropServer/Events/`](../RaindropServer/Events/); the endpoint is
[`ServerCli/Endpoints/Events/EventStreamEndpoint.cs`](../ServerCli/Endpoints/Events/EventStreamEndpoint.cs),
a one-liner over the library's WebSocket transport.

## The model in one paragraph

Events are **notifications, not a source of truth**. Every event carries the full current state of
what it is about (the whole message, the tombstone, the channel as *you* see it), so a client merges
it by id exactly as it would a page it fetched. Nothing is replayed: a client that was disconnected
catches up by re-reading after it reconnects. That keeps the server stateless about who has seen what,
and it means a missed or duplicated event can never leave a client wrong — only briefly stale.

## Connecting

```
GET /api/events
Authorization: Bearer <token>
Accept: application/x-msgpack        (optional — JSON otherwise)
```

- **Authentication** is the same `Authorization: Bearer` header as every other call. The token is never
  put in the URL, for the same reason nothing else is — see
  [Transport security](transport-security.md). A missing or invalid token is a `401` before the upgrade.
- **Wire format** is negotiated from `Accept` exactly as a response body's is (see
  [Wire formats](wire-formats.md)). JSON arrives in **text** frames, MessagePack in **binary** frames.
- **HTTP/1.1 or HTTP/2.** Over HTTP/2 the WebSocket is an extended `CONNECT` (RFC 8441) and rides the
  connection the client already has open for REST calls. `ServerHost` maps every `GET` route for
  `CONNECT` as well, which is what makes that work.
- A plain `GET` that is not a WebSocket upgrade is answered `426 websocket_required`.

The stream is **server → client only**. Requests still go through the REST API; a client that sends a
data frame has its connection closed with `1008`.

### Recommended client sequence

1. Open the stream.
2. Wait for `ready` — it is always the first frame.
3. *Then* re-read what the screen shows (channel list, roster, the newest page of the open channel)
   and merge it with anything that has arrived on the stream since. Reading after `ready` rather than
   before is what guarantees no gap: anything that changed after the read arrives as an event.
4. On any close, reconnect with backoff and go back to step 2. After a `4401`, log in first.

## Frames

Every frame is one envelope:

```json
{ "type": "message.created", "data": { … } }
```

In MessagePack the keys are `Type` and `Data`, and the payload keys are PascalCase — the usual rule,
see [Wire formats](wire-formats.md#messagepack-specifics).

Types are **names**, like permissions and presence: a client too old to know one ignores it.

| Type | Data | Sent to |
|---|---|---|
| `ready` | `ReadyEvent(UserId, ProtocolVersion)` | the new connection, first, once |
| `message.created` | `ChannelMessage` — as in history | members who can see the channel |
| `message.deleted` | `ChannelMessage` — the tombstone, `content: null`, `deleted: true` | members who can see the channel |
| `channel.created` | `ChannelSummary` — with the **recipient's own** `yourPermissions` | members who can see it |
| `channel.renamed` | `ChannelSummary` — with the recipient's own `yourPermissions` | members who can see it |
| `channel.deleted` | `ChannelDeletedEvent(ChannelId)` | members who could see it |
| `member.joined` | `MemberEvent(UserId, Username)` — registered or rejoined | everyone |
| `member.renamed` | `MemberEvent(UserId, Username)` | everyone |
| `member.left` | `MemberEvent(UserId, Username)` — `Username` null once erased | everyone |
| `member.presence` | `MemberPresenceEvent(UserId, Presence)` — `online`/`offline` | everyone |
| `member.roles` | `MemberRolesEvent(UserId, RoleId, Assigned)` | everyone |
| `role.created` | `RoleSummary` | everyone |
| `role.updated` | `RoleSummary` | everyone |
| `role.deleted` | `RoleDeletedEvent(RoleId)` | everyone |
| `voice.state` | `VoiceParticipant` — somebody joined a call, or their state in it changed; merge on `(channelId, userId)` | members who can see the channel, and the member themselves |
| `voice.left` | `VoiceLeftEvent(ChannelId, UserId, Reason)` — `Reason` is an open string, see [Voice](voice.md#voice-api) | members who can see the channel, and the member themselves |

Payloads with no REST counterpart are in
[`ServerEvent.cs`](../RaindropServer/Events/ServerEvent.cs); the rest are the same records the REST API
returns.

Things worth knowing:

- **Who hears what is decided by the same resolver as every REST check.** A channel you cannot see
  produces no events at all — not even `channel.deleted`, which would give away that it existed.
- **Voice events reach the member they are about, even when they can no longer see the channel** —
  somebody removed from a call for losing `view_channel` has to hear why.
- **Role events can change what you may do.** After any `role.*` or a `member.roles` about yourself,
  re-read `GET /api/channels` (a channel may have appeared or gone) and `GET /api/roles/me`. The server
  does not try to work out which channels became visible to whom; it would be re-deriving the
  resolver's answer for every member on every role edit.
- **`member.presence` is a state, not a transition.** It is sent after every session change, so a
  second device logging in re-announces `online`. Treat it as "set", never as "toggle".
- **Order is per connection, not global.** Events from two requests running at once can arrive in
  either order. Because every payload carries full state and deletes are tombstones, merging by id
  and keeping the most-deleted version is always correct.
- **You receive your own actions too.** A client that posts a message gets `message.created` for it,
  possibly before the REST response. Both carry the same `messageId`, so merging by id shows it once;
  the optimistic local copy is retired when the REST response arrives, as it is today.

## Closing

| Code | Description | Means | Client should |
|---|---|---|---|
| `1000` | `closed` | client closed | — |
| `1001` | `server_stopping` | the server is shutting down | reconnect with backoff |
| `1001` | `connection_lost` | the network went away | reconnect with backoff |
| `1008` | `server_events_only` | the client sent a data frame | fix the client |
| `4401` | `session_ended` | logged out, password changed, left, or lapsed | log in, then reconnect |
| `4429` | `too_slow` | more than `MaximumPendingEvents` queued for it | reconnect and re-read |

`4401` and `4429` sit in the application range and are numbered after the HTTP status that means the
same thing. The mapping is `WebSocketCloseCodes`, from the transport-independent `EventCloseReason`.

A password change revokes every session, *including the one that made the change* — it gets a new
token back — so that client's own stream closes `4401` too, and reopens with the new token.

## Sessions and the stream

- **An open stream keeps its session alive.** Sessions lapse after `SessionIdleTimeout` without a
  request; a client with a stream open is plainly still there, so the registry renews each open
  connection's session every `SessionRenewalInterval` (5 minutes, capped at half the idle timeout).
- **A revoked session stops hearing anything at once.** `UserService` tells every `ISessionListener`
  when a member's sessions change, and the hub closes the streams whose tokens are gone. As a second
  line of defence, every publish checks `ISessionStore.IsActive` — a non-sliding check, so a busy
  server cannot keep an abandoned session alive just by talking to it — before delivering anything.
- **Leaving closes everything**, since leaving revokes every session.

## Architecture, and the seams for plugins

The event system is built as a set of replaceable pieces, because the same seams are what a plugin
SDK will expose. Each is a separate DI registration (all `TryAdd*`, so registering your own first wins).

```
 UserService ─┐                                    ┌─► IEventConnection (WebSocket) ─► client
 ChannelService ├─► IEventPublisher ─► EventHub ───┼─► IEventConnection (WebSocket) ─► client
 TextChannelService   (any plugin too)     │       └─► IEventConnection (your transport)
 RoleService ─┘                            │
 PresenceBroadcaster ─┘                    └─► IServerEventHandler<T> (in-process: plugins)
```

| Seam | What it is | Default | Replace or add it to… |
|---|---|---|---|
| [`IEventPublisher`](../RaindropServer/Events/IEventPublisher.cs) | how anything announces a change | `EventHub` | relay events through something shared, if a server is ever more than one process |
| [`IEventConnectionRegistry`](../RaindropServer/Events/IEventConnection.cs) | where transports hand their connections | `EventHub` | hold connections elsewhere |
| [`IEventConnection`](../RaindropServer/Events/IEventConnection.cs) | one listening client, whatever carries it | `WebSocketEventConnection` | add a transport — SSE, a push gateway — without touching publishers |
| [`IServerEventHandler<T>`](../RaindropServer/Events/IServerEventHandler.cs) | server code reacting to events | none | react in-process: a bot, an audit log, a webhook relay |
| [`EventAudience`](../RaindropServer/Events/IEventPublisher.cs) | who may hear one event | everyone | `ChannelService.AudienceFor(channel)`, `EventAudiences.Only(...)`, or your own |
| [`ISessionListener`](../RaindropServer/Users/ISessionListener.cs) | told when a member's sessions change | `EventHub`, `PresenceBroadcaster` | anything else that follows logins |
| [`IMessageDeletionListener`](../RaindropServer/Channels/Text/IMessageDeletionListener.cs) | told, *before* the text is destroyed, that a message or a whole channel's messages are being deleted | `ArchivingMessageDeletionListener` | forward or preserve deleted messages elsewhere. Not an event: it runs before the delete, and a listener that throws **stops** it. See [Messages](messages.md#the-deleted-message-archive) |

**Publishing your own event** is one call; types are open strings, and prefixing them with your
plugin's name keeps them from colliding (`polls.vote_cast`):

```csharp
await events.PublishAsync("polls.vote_cast", new VoteCast(pollId, userId), channels.AudienceFor(channel));
```

`PublishPerRecipientAsync` is the variant for payloads that differ per member (the channel events use
it for `yourPermissions`); its projection returns null to leave a member out.

**Reacting to events in-process:**

```csharp
services.AddServerEventHandler<ChannelMessage, MyBot>();

sealed class MyBot : IServerEventHandler<ChannelMessage>
{
    public Task HandleAsync(ServerEvent<ChannelMessage> e, CancellationToken ct) =>
        e.Type == ServerEventTypes.MessageCreated ? ReplyAsync(e.Data, ct) : Task.CompletedTask;
}
```

Handlers get every event of their payload type **regardless of audience** — they are server code, not a
member — so a handler that repeats what it hears to members must apply the visibility rules itself.
They run inside the publish, after clients have been queued, so the request that caused the event
waits for them: keep them quick and queue anything slow. For per-recipient events a handler receives
the version that belongs to nobody (a `ChannelSummary` with empty `yourPermissions`).

**Adding a transport** means implementing `IEventConnection` — `Deliver` and `Close` must never block,
since they run on the publishing request's thread — and calling `IEventConnectionRegistry.Register`
once the connection is open, disposing the result when it closes. The registry sends `ready` first,
closes the connection when its session ends, keeps its session renewed, and closes it on shutdown, so a
transport gets all of that for free. `WebSocketEventStream` is the reference implementation.

### Guarantees the hub keeps

- **Publishing never fails the caller.** The change is already written when it is announced, so an
  audience check, a projection or a handler that throws is reported and stepped over.
- **Publishing never waits on a client.** Each connection has a bounded queue
  (`MaximumPendingEvents`); a full queue closes it `4429` instead of blocking or buffering without limit.
- **An event is encoded once per wire format**, however many connections hear it (per member, for
  per-recipient events).
- **The publishing request's cancellation is ignored** for delivery — a caller hanging up straight after
  a write must not stop everyone else hearing about it.

Like the session store and the permission cache, the hub keeps its state in memory, which is correct
because a Raindrop server is a single process.

## Options

`EventOptions`, passed as `configureEvents` to `AddRaindropServer` (not yet read from `raindrop.json`):

| Option | Default | Meaning |
|---|---|---|
| `MaximumPendingEvents` | 256 | queued frames per connection before it is closed `4429` |
| `KeepAliveInterval` | 30 s | WebSocket ping interval, so proxies and NAT do not drop idle streams |
| `SessionRenewalInterval` | 5 min | how often an open stream renews its session (capped at half the idle timeout) |
| `CloseTimeout` | 2 s | how long a close handshake may take — well inside the 5 s shutdown budget |

## What it does not do (yet)

- **No replay or resume.** There is no sequence number to resume from; reconnecting means re-reading.
- **A lapsed session is not announced.** Sessions lapse lazily inside the store, with nobody to tell, so
  a member whose client died without logging out reads as online until the idle timeout — exactly as
  the roster has them. An open stream is renewed, so this only affects members with no stream open.
- **No subscription filtering.** Every connection receives every event it is allowed to see; a client
  ignores channels it is not showing. Fine at the size a Raindrop server is.

## Related

- The client's [Live updates doc](../../RaindropClient/docs/live-updates.md) — how the Flutter client
  follows this stream: reconnect and backoff, re-reading after `ready`, fallback polling.
- [Wire formats](wire-formats.md) — how frames are encoded.
- [Users](users.md) — sessions, and `ISessionListener`.
- [Voice](voice.md) — what `voice.state` / `voice.left` describe, and the in-process handlers
  (`VoicePermissionWatcher`) voice itself registers.
- [Members](members.md) — how presence is derived and announced.
- [Endpoint model & DI wiring](endpoint-model.md#addraindropeventsconfigure) — `AddRaindropEvents`.
