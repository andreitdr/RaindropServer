# Events (push)

The server pushes changes to connected clients as they happen, over a bidirectional gRPC stream:
`raindrop.v1.EventService/Connect`. With the stream open, a client no longer needs to poll. A message
posted, a channel renamed, a member coming online or a role handed out reaches everyone allowed to see
it within the same call that caused it.

The code is in [`RaindropServer/Events/`](../RaindropServer/Events/), which is transport-independent.
The gRPC transport is in [`RaindropServer.Grpc/Events/`](../RaindropServer.Grpc/Events/), with the
service in [`EventGrpcService`](../RaindropServer.Grpc/Services/EventGrpcService.cs). The messages are
in RaindropProto's [`events.proto`](../../RaindropProto/raindrop/v1/events.proto).

## The model in one paragraph

Events are **notifications, not a source of truth**. Every event carries the full current state of
what it is about: the whole message, the tombstone, the channel as *you* see it. A client merges it by
id, exactly as it would a page it fetched. Nothing is replayed: a client that was disconnected catches
up by re-reading after it reconnects. That keeps the server stateless about who has seen what, and it
means a missed or duplicated event can never leave a client wrong, only briefly stale.

## Connecting

```
rpc Connect(stream ConnectRequest) returns (stream ConnectResponse);
authorization: Bearer <token>
```

- **Authentication** uses the same `authorization` metadata as every other call. A missing or invalid
  token fails the call with `UNAUTHENTICATED` (`missing_token` / `invalid_session`) before anything is
  sent.
- **One connection.** The stream rides the HTTP/2 connection the client already has open for its calls.

### What the client sends

`ConnectRequest` is a `oneof`:

| Frame | Meaning |
|---|---|
| `Heartbeat` | "still here". Send one every `ready.heartbeat_interval` (20 s by default). |
| `Resubscribe` | Asks for a fresh `ready` on the same stream, e.g. after the app was suspended, so the client re-reads without reconnecting. It also counts as a heartbeat. |

A frame with neither case set is a client bug, and ends the stream with `invalid_frame`. Ordinary
actions (sending a message, committing, joining a call) are **not** sent here. They stay unary calls,
where every action has its own status and retry.

### Recommended client sequence

1. Open the stream, and start the heartbeat timer.
2. Wait for `ready`. It is always the first event.
3. *Then* re-read what the screen shows (channel list, roster, the newest page of the open channel),
   and merge it with anything that has arrived on the stream since. Reading after `ready` rather than
   before is what guarantees there is no gap: anything that changed after the read arrives as an event.
4. When the stream ends, act on its reason (see [How a stream ends](#how-a-stream-ends)): reconnect
   with backoff and go back to step 2, or log in first after `session_ended`.

## Events

Each `ConnectResponse` holds one `ServerEvent`, whose `oneof` says which change it is. A client too
old to know a case ignores it. Inside the server events still travel by name (`message.created`, …),
which is how a plugin adds its own. `ProtobufEventEncoder` maps each built-in name to its case.

| Case (name inside the server) | Message | Sent to |
|---|---|---|
| `ready` (`ready`) | `ReadyEvent`: user id, protocol version, heartbeat interval | the new connection, first; again after a `Resubscribe` |
| `message_created` (`message.created`) | `ChannelMessage`, as in history, with `attachments` when it carries [files](attachments.md) | members who can see the channel |
| `message_deleted` (`message.deleted`) | `ChannelMessage`: the tombstone, no content, `deleted` | members who can see the channel |
| `message_reported` (`message.reported`) | `MessageReport`: an encrypted message a member reported, its text proven by the author's attestation. See [Messages](messages.md#reporting-an-encrypted-message) | members with `manage_messages` in the channel |
| `channel_created` (`channel.created`) | `Channel`, with the **recipient's own** `your_permissions` | members who can see it |
| `channel_renamed` (`channel.renamed`) | `Channel`, with the recipient's own `your_permissions` | members who can see it |
| `channel_deleted` (`channel.deleted`) | `ChannelDeletedEvent` | members who could see it |
| `member_joined` (`member.joined`) | `MemberEvent`: registered or rejoined | everyone |
| `member_renamed` (`member.renamed`) | `MemberEvent` | everyone |
| `member_left` (`member.left`) | `MemberEvent`; no username once erased | everyone |
| `member_presence` (`member.presence`) | `MemberPresenceEvent`: `PRESENCE_ONLINE` / `PRESENCE_OFFLINE` | everyone |
| `member_roles` (`member.roles`) | `MemberRolesEvent` | everyone |
| `role_created` / `role_updated` (`role.created` / `role.updated`) | `Role` | everyone |
| `role_deleted` (`role.deleted`) | `RoleDeletedEvent` | everyone |
| `voice_state` (`voice.state`) | `VoiceParticipant`: somebody joined a call, or their state in it changed; merge on `(channel_id, user_id)` | members who can see the channel, and the member themselves |
| `voice_left` (`voice.left`) | `VoiceLeftEvent`; `reason` is an open string, see [Voice](voice.md#voice-api) | members who can see the channel, and the member themselves |
| `device_added` / `device_revoked` (`e2ee.device.added` / `.revoked`) | `DeviceEvent`. See [E2EE](e2ee.md#events) | everyone |
| `identity_reset` (`e2ee.identity.reset`) | `IdentityResetEvent`: that member's safety number changed | everyone |
| `handshake` (`e2ee.handshake`) | `HandshakeEvent`: an accepted MLS commit, see [E2EE](e2ee.md#the-delivery-service) | members allowed in the channel's group |
| `welcome` (`e2ee.welcome`) | `WelcomeEvent`: a Welcome waits in `DeliveryService.ListWelcomes` | the recipient |
| `group_reconcile` (`e2ee.group.reconcile`) | `GroupReconcileEvent`: compare the group with its roster | members allowed in the channel's group |
| `key_packages_low` (`e2ee.keypackages.low`) | `KeyPackagesLowEvent` | the device's owner |
| `plugin` (any other name) | `PluginEvent`: the plugin's type name and its own protobuf message as `data` | whoever the plugin's audience says |

Things worth knowing:

- **Who hears what is decided by the same resolver as every call.** A channel you cannot see produces
  no events at all. That includes `channel_deleted`, which would otherwise give away that it existed.
- **Voice events reach the member they are about, even when they can no longer see the channel.**
  Somebody removed from a call for losing `view_channel` has to hear why.
- **Role events can change what you may do.** After any `role_*` event, or a `member_roles` about
  yourself, re-read `ListChannels` (a channel may have appeared or gone) and `GetMyRoles`. The server
  does not try to work out which channels became visible to whom; that would mean re-deriving the
  resolver's answer for every member on every role edit.
- **`member_presence` is a state, not a transition.** It is sent whenever a member's first stream opens
  or last one closes, and after every session change. Treat it as "set", never as "toggle".
- **Order is per connection, not global.** Events from two calls running at once can arrive in either
  order. Every payload carries full state and deletes are tombstones, so merging by id and keeping the
  most-deleted version is always correct.
- **You receive your own actions too.** A client that posts a message gets `message_created` for it,
  possibly before the call returns. Both carry the same `message_id`, so merging by id shows it once.

## How a stream ends

While the server runs, the stream only ends with a status. Its `ErrorInfo.reason` says what to do (see
[gRPC services](grpc-services.md#errors)):

| Reason | Status | Means | Client should |
|---|---|---|---|
| `session_ended` | `UNAUTHENTICATED` | logged out, password changed, left, or lapsed | log in, then reconnect |
| `too_slow` | `RESOURCE_EXHAUSTED` | more than `MaximumPendingEvents` queued for it | reconnect and re-read |
| `server_stopping` | `UNAVAILABLE` | the server is shutting down | reconnect with backoff |
| `connection_lost` | `UNAVAILABLE` | no heartbeat for `HeartbeatTimeout`, or the connection broke | reconnect with backoff |
| `invalid_frame` | `INVALID_ARGUMENT` | the client sent something other than a heartbeat or resubscribe | fix the client |

The client ending its own half of the stream is a normal close, so the server ends the call with OK.
`GrpcEventConnection.Ending` maps the transport-independent `EventCloseReason` to these statuses.

A password change revokes every session, *including the one that made the change*; that call gets a
new token back. So that client's own stream ends `session_ended` too, and reopens with the new token.

## Heartbeats, sessions and presence

- **Heartbeats make presence honest.** A member is online exactly while they have a stream open
  (`StreamPresenceTracker`, see [Members](members.md)). A stream whose client stops heartbeating, for
  example a laptop closed or an app killed, is ended as `connection_lost` once `HeartbeatTimeout`
  passes (60 s by default). The member then reads as offline about a minute later, instead of when
  their session finally lapses. The registry tells every `IEventStreamListener` when a member's first
  stream opens and when their last one closes, and `PresenceBroadcaster` announces it.
- **An open stream keeps its session alive.** Sessions lapse after `SessionIdleTimeout` without a call.
  A client with a stream open is plainly still there, so the registry renews each open connection's
  session every `SessionRenewalInterval` (5 minutes, capped at half the idle timeout). Because dead
  streams are cut off by the heartbeat timeout, this renewal is only ever on behalf of a live client.
- **A revoked session stops hearing anything at once.** `UserService` tells every `ISessionListener`
  when a member's sessions change, and the hub ends the streams whose tokens are gone. As a second line
  of defence, every publish checks `ISessionStore.IsActive` before delivering anything. That check does
  not slide the session, so a busy server cannot keep an abandoned session alive just by talking to it.
- **Leaving closes everything**, since leaving revokes every session.

## Architecture, and the seams for plugins

The event system is built as a set of replaceable pieces, because the same seams are what a plugin
SDK will expose. Each is a separate DI registration, all `TryAdd*`, so registering your own first wins.

```
 UserService ─┐                                    ┌─► IEventConnection (gRPC stream) ─► client
 ChannelService ├─► IEventPublisher ─► EventHub ───┼─► IEventConnection (gRPC stream) ─► client
 TextChannelService   (any plugin too)     │       └─► IEventConnection (your transport)
 RoleService ─┘                            │
 PresenceBroadcaster ─┘                    └─► IServerEventHandler<T> (in-process: plugins)
```

| Seam | What it is | Default | Replace or add it to… |
|---|---|---|---|
| [`IEventPublisher`](../RaindropServer/Events/IEventPublisher.cs) | how anything announces a change | `EventHub` | relay events through something shared, if a server is ever more than one process |
| [`IEventConnectionRegistry`](../RaindropServer/Events/IEventConnection.cs) | where transports hand their connections; also answers who is connected, and re-sends `ready` | `EventHub` | hold connections elsewhere |
| [`IEventConnection`](../RaindropServer/Events/IEventConnection.cs) | one listening client, whatever carries it | `GrpcEventConnection` | add a transport, such as a push gateway, without touching publishers |
| [`IEventEncoder`](../RaindropServer/Events/IEventConnection.cs) | turns an event into a transport's frame; connections sharing an encoder share each frame | `ProtobufEventEncoder` | encode for your own transport |
| [`IServerEventHandler<T>`](../RaindropServer/Events/IServerEventHandler.cs) | server code reacting to events | none | react in-process: a bot, an audit log, a webhook relay |
| [`IEventStreamListener`](../RaindropServer/Events/IEventConnection.cs) | told when a member's first stream opens or last one closes | `PresenceBroadcaster` | anything else that follows who is listening |
| [`EventAudience`](../RaindropServer/Events/IEventPublisher.cs) | who may hear one event | everyone | `ChannelService.AudienceFor(channel)`, `EventAudiences.Only(...)`, or your own |
| [`ISessionListener`](../RaindropServer/Users/ISessionListener.cs) | told when a member's sessions change | `EventHub`, `PresenceBroadcaster` | anything else that follows logins |
| [`IMessageDeletionListener`](../RaindropServer/Channels/Text/IMessageDeletionListener.cs) | told, *before* the text is destroyed, that a message or a whole channel's messages are being deleted | `ArchivingMessageDeletionListener` | forward or preserve deleted messages elsewhere. Not an event: it runs before the delete, and a listener that throws **stops** it. See [Messages](messages.md#the-deleted-message-archive) |

**Publishing your own event** takes one call. Types are open strings, and prefixing them with your
plugin's name keeps them from colliding (`polls.vote_cast`). To reach clients, the payload must be a
**protobuf message**, which the plugin defines in its own `.proto`. It travels as
`PluginEvent { type, data }`, and clients without the plugin ignore it:

```csharp
await events.PublishAsync("polls.vote_cast", new VoteCast { PollId = pollId }, channels.AudienceFor(channel));
```

A payload that is not a protobuf message still reaches in-process handlers. The stream cannot carry
it, so the encoder logs that once per type and clients do not receive it.

`PublishPerRecipientAsync` is the variant for payloads that differ per member; the channel events use
it for `your_permissions`. Its projection returns null to leave a member out.

**Reacting to events in-process:**

```csharp
services.AddServerEventHandler<ChannelMessage, MyBot>();

sealed class MyBot : IServerEventHandler<ChannelMessage>
{
    public Task HandleAsync(ServerEvent<ChannelMessage> e, CancellationToken ct) =>
        e.Type == ServerEventTypes.MessageCreated ? ReplyAsync(e.Data, ct) : Task.CompletedTask;
}
```

Handlers get every event of their payload type **regardless of audience**. They are server code, not
a member, so a handler that repeats what it hears to members must apply the visibility rules itself.
They run inside the publish, after clients have been queued, so the call that caused the event waits
for them: keep them quick and queue anything slow. For per-recipient events a handler receives the
version that belongs to nobody (a `ChannelSummary` with empty `YourPermissions`).

**Adding a transport** means implementing `IEventConnection` and an `IEventEncoder`:
- `Deliver` and `Close` must never block, since they run on the publishing call's thread.
- Call `IEventConnectionRegistry.Register` once the connection is open, and dispose the result when it
  closes.

The registry then sends `ready` first, closes the connection when its session ends, keeps its session
renewed, counts it for presence, and closes it on shutdown, so a transport gets all of that for free.
`GrpcEventConnection` is the reference implementation.

### Guarantees the hub keeps

- **Publishing never fails the caller.** The change is already written when it is announced, so an
  audience check, a projection or a handler that throws is reported and stepped over.
- **Publishing never waits on a client.** Each connection has a bounded queue (`MaximumPendingEvents`).
  A full queue ends the stream as `too_slow` instead of blocking or buffering without limit.
- **An event is encoded once per encoder**, however many connections hear it (per member, for
  per-recipient events). Every gRPC stream shares one protobuf message.
- **The publishing call's cancellation is ignored** for delivery. A caller hanging up straight after a
  write must not stop everyone else hearing about it.

Like the session store and the permission cache, the hub keeps its state in memory. That is correct
because a Raindrop server is a single process.

## Options

`EventOptions`, passed as `configureEvents` to `AddRaindropServer` (not yet read from `raindrop.json`):

| Option | Default | Meaning |
|---|---|---|
| `MaximumPendingEvents` | 256 | queued events per connection before it ends as `too_slow` |
| `HeartbeatInterval` | 20 s | how often clients are told to heartbeat (in `ready`); short enough for proxies and NAT to keep the stream |
| `HeartbeatTimeout` | 60 s | silence after which a stream is `connection_lost`, and its member offline |
| `SessionRenewalInterval` | 5 min | how often an open stream renews its session (capped at half the idle timeout) |

## What it does not do (yet)

- **No replay or resume.** There is no sequence number to resume from; reconnecting means re-reading.
- **No subscription filtering.** Every connection receives every event it is allowed to see, and a
  client ignores channels it is not showing. That is fine at the size a Raindrop server is. If it is
  ever needed, the client half of the stream is where a subscription request would go.

## Related

- The client's [Live updates doc](../../RaindropClient/docs/live-updates.md): how the Flutter client
  follows this stream (heartbeats, reconnect and backoff, re-reading after `ready`).
- [Wire format](wire-formats.md) and RaindropProto's [streams doc](../../RaindropProto/docs/streams.md).
- [Users](users.md): sessions, and `ISessionListener`.
- [Voice](voice.md): what `voice_state` / `voice_left` describe, and the in-process handlers
  (`VoicePermissionWatcher`) voice itself registers.
- [Members](members.md): how presence is derived and announced.
- [gRPC services & DI wiring](grpc-services.md#addraindropeventsconfigure): `AddRaindropEvents`.
