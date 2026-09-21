# Voice channels

A voice channel is a group call: members join and leave whenever they like, and everyone in it hears
everyone else. Members can mute themselves, deafen themselves (stop hearing anyone, which also mutes),
and a moderator can **mute somebody for everyone**. Per-member volume and "mute just for me" are
client-side and never reach the server.

Code: `RaindropServer/Channels/Voice/` (transport-agnostic), `RaindropServer.Voice.LiveKit/` (the
media transport), `ServerCli/Endpoints/Channels/Voice/` (HTTP surface).

## Who does what

Raindrop is the **authority**; a media server only **carries audio**.

| Raindrop decides | The media server does |
|---|---|
| who may see, join and speak in a channel (roles and permissions) | receive each participant's microphone |
| who is in which call, and their mute/deafen state | forward it to everybody else in the room (an SFU) |
| moderator mutes, stored so they survive restarts | refuse a microphone its token does not allow |

The media server shipped with Raindrop is **LiveKit**, running as a sidecar next to the server. It
sits behind the `IVoiceTransport` seam, so nothing in `VoiceService` knows which one it is talking to.
The join response names the transport (`"transport": "livekit"`), and a client picks its engine by
that name.

```
client ──REST + /api/events (pinned TLS)──────────▶ Raindrop ──Twirp JSON (private HTTP)──▶ LiveKit
   │                                                    ▲  VoiceService: permissions, roster, mutes
   └──LiveKit signalling ──▶ /api/channels/voice/signal/rtc  (relayed by Raindrop, same pinned TLS)
   └──audio (DTLS-SRTP over UDP 7882, TCP 7881 fallback) ─────────────────────────────────▶ LiveKit
```

## Permissions

Voice uses the same server-wide roles as everything else (see
[Roles & permissions](roles-and-permissions.md)):

| Permission | Wire name | Effect |
|---|---|---|
| `ViewChannel` | `view_channel` | See the voice channel at all. Without it the channel does not exist for you, and neither does its call. |
| `Connect` | `connect` | Join the call. |
| `Speak` | `speak` | Be heard. Without it you join as a listener: the participant is `suppressed`, and LiveKit refuses your microphone. |
| `MuteMembers` | `mute_members` | Mute or unmute somebody for everyone. Checked **server-wide**, never per channel, and not part of the channel-creator rule, because a mute follows its target into every call. |

`connect` and `speak` are in the `everyone` role by default. An existing server gains them once
through a **permission rollout**; see below.

Permissions are enforced by the media server, not just the UI:

- A join token's `canPublish` is `speak && !mutedForEveryone`.
- `canPublishSources` is limited to the microphone. Camera and screen share come later, by widening it.
- A change mid-call is pushed with LiveKit's `UpdateParticipant`. Losing `view_channel` or `connect`
  mid-call removes the participant with reason `permission_lost`.

**Nobody may mute an administrator for everyone unless they are one.** That is the same spirit as "you
cannot hand out what you do not hold". It can never lock anybody out, because an administrator can
always unmute themselves.

### Permission rollouts

`RoleOptions.DefaultMemberPermissions` only seeds a **new** server, so an existing server upgraded to
a build with voice would otherwise have nobody but its administrators able to join a call.
`PermissionRollouts.All` (`Roles/PermissionRollouts.cs`) fixes that. It lists permissions a build
introduces for everybody: `new("voice", Connect | Speak)`.

- On startup, `EnsureBuiltInRolesAsync` ORs each rollout not yet recorded into the `everyone` role,
  then records it in `raindrop_permission_rollouts`.
- Each rollout is **offered exactly once**, so an owner who removes those permissions afterwards keeps
  them removed.
- A fresh server records every rollout without granting anything, since its seed already reflects this
  build.
- The list lives in code, not configuration: the shipped `raindrop.json` names default permissions
  explicitly, and an owner's older copy of it must not decide whether members ever receive a new one.

## State

**Who is in which call** lives in memory (`IVoiceStateStore`, default `InMemoryVoiceStateStore`), like
sessions. A call cannot outlive the process that admitted its participants, because their sessions do
not outlive it either.

- The store's operations are whole decisions (`JoinAsync(record, capacity)` returns
  full / moved-from / replaced). That enforces capacity and **one call per member per server**
  atomically.
- Each record keeps the **session token** that joined. Logging out on one device ends that device's
  call and no other.

**Mutes for everyone** are stored in `raindrop_voice_mutes` (`IVoiceMuteStore`, one row per muted
member). They survive restarts and rejoins, and can be lifted while the member is not in a call.

## Keeping Raindrop and LiveKit in step

There are no webhooks. LiveKit's webhooks would have to call into Raindrop's self-signed HTTPS, which
LiveKit does not trust without extra setup. Instead:

- **Join and leave are REST calls**, so the roster changes the instant somebody clicks. A joined member
  is *pending* (`connected: false`) until LiveKit reports them.
- **`VoiceMaintenanceService`** is a single background loop that does four things:
  - **At startup**, it deletes every Raindrop room left on LiveKit. A restart logged everybody out, so
    nothing can be adopted.
  - **Queued work**: pushing permission changes, and disconnecting somebody who left.
  - **Every `ReconcileInterval` (5 s)**, it compares Raindrop's record with LiveKit's, in both
    directions:
    - pending becomes connected once LiveKit reports the participant;
    - a participant LiveKit has not reported for `JoinGrace` (20 s) is dropped (`disconnected`);
    - a participant whose session is no longer active is dropped (`session_ended`);
    - anybody in a Raindrop room whom Raindrop did not admit is removed. **Self-hosted LiveKit does not
      revoke a token when its holder is removed**, so this sweep and the short token lifetime (2 min)
      are what keep a removed member from walking back in. LiveKit refreshes the token of a connected
      participant, so long calls are unaffected;
    - a `canPublish` that has drifted is corrected;
    - a room whose channel no longer exists is closed.
  - **On shutdown**, it ends every call, best-effort, within 3 s.

  A LiveKit outage is logged once, then retried quietly until it recovers. Queued work and
  reconciliation share the one loop, so they never race each other.
- **Role changes** (`role.updated`, `role.deleted`, `member.roles`, `member.left`) queue a re-check of
  every participant (`VoicePermissionWatcher`), coalesced so a burst of changes costs one pass.
- **Session changes** (logout, password change, leaving) queue a re-check of that member
  (`VoiceSessionListener`).
- **Deleting a voice channel** ends its call (`VoiceChannelModule`, reason `channel_deleted`). This is
  best-effort against LiveKit, unlike the fail-closed message archive: a media-server outage must never
  stop a channel from being deleted.

## The signalling relay

Clients never talk to LiveKit's HTTP port. The join response gives `path:
/api/channels/voice/signal`, and the client connects to `wss://<the server it already
pins>/api/channels/voice/signal`. LiveKit's SDK then opens `…/rtc` and, after a failure,
`…/rtc/validate`. `LiveKitSignalRelay` forwards those two paths, and nothing else, to LiveKit.

- **One certificate.** The signalling connection rides the same pinned, often self-signed, TLS
  certificate as everything else, so owners need no second certificate. Only LiveKit's media ports
  face the internet.
- **An enforcement point.** A connection is forwarded only for a token Raindrop signed (or LiveKit
  refreshed), naming a member Raindrop has admitted to *that* room on a session that is still active.
  Anything else gets `401` with a plain-text reason, which LiveKit's client shows.
- The endpoint is a raw `IEndpoint` (`VoiceSignalRelayEndpoint`, route
  `/api/channels/voice/signal/{**path}`), because the caller authenticates with LiveKit's token, not a
  Raindrop session. GET routes also answer CONNECT, so it works over HTTP/2 too.

Media is encrypted with DTLS-SRTP **as far as LiveKit**, which decrypts it to forward it. So the
server owner can, in principle, hear calls. See [Transport security](transport-security.md).

## Voice API

All authenticated.

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/channels/voice` | `{available, transport, maximumParticipants, participants[]}`: whether voice works here, and everybody in every call the caller can see |
| POST | `/api/channels/voice/join` | `{channelId, selfMute?, selfDeaf?}` → `{channelId, transport, url, path, token, expiresAtUtc, you, participants[]}`. Needs `connect`. Joining another call leaves the first; joining the same call from another device takes it over. |
| POST | `/api/channels/voice/leave` | `{channelId}` → `{left}`. Idempotent, and only the session that joined can leave, so a late leave from one device cannot end another device's call. |
| POST | `/api/channels/voice/state` | `{selfMute, selfDeaf}` → `VoiceParticipant`. Deafening always mutes too. |
| POST | `/api/channels/voice/mute` | `{userId, muted}` → `{userId, muted, changed}`. Mute or unmute for everyone; needs server-wide `mute_members`. |
| GET | `/api/channels/voice/signal/{path}` | The LiveKit signalling relay (see above). Not a Raindrop-session endpoint. |

`VoiceParticipant` is `{channelId, userId, selfMute, selfDeaf, serverMute, suppressed, connected,
joinedAtUtc}`: the whole state, merged by clients on `(channelId, userId)`. `url` is null in this
version (everything goes through the relay). It is in the contract so a transport can point clients
at a media server with its own TLS later.

Error codes on top of the channel ones:

| Code | Status | When |
|---|---|---|
| `voice_unavailable` | 503 | No media server is configured (`voice.provider` is null) |
| `channel_full` | 409 | The call already holds `maximumParticipants` other members |
| `not_in_voice` | 409 | `state` from a session that is not in a call |
| `member_not_found` | 404 | `mute` for somebody who is not an active member |
| `missing_permission` | 403 | No `connect`, no `mute_members`, or muting an administrator without being one |
| `wrong_channel_type` | 400 | Joining a text channel |

Events: `voice.state` (a `VoiceParticipant`) and `voice.left` (`{channelId, userId, reason}`), sent to
everyone who can see the channel **plus the member themselves**, so somebody removed because they lost
sight of the channel still hears about it. Reasons are open strings: `left`, `moved`, `disconnected`,
`replaced`, `permission_lost`, `session_ended`, `channel_deleted`, `server_ended`. Treat unknown ones
as `left`. See [Events](events.md).

## Configuration

```jsonc
"voice": {
  "provider": null,              // "livekit" to switch voice on
  "maximumParticipants": 25,
  "livekit": { "apiUrl": "http://localhost:7880", "apiKey": "devkey", "apiSecret": "secret" }
}
```

Environment overrides: `RAINDROP_VOICE_PROVIDER`, `RAINDROP_LIVEKIT_API_URL`,
`RAINDROP_LIVEKIT_API_KEY`, `RAINDROP_LIVEKIT_API_SECRET`. Prefer the environment variable to writing
the secret into the file.

With `provider: null`, voice channels can still be created and deleted, but joining is refused with
`voice_unavailable`, and `GET /api/channels/voice` reports `available: false`. `livekit` with a
missing key or secret is a startup error. The console's `voice` command lists who is in which call.

### Running LiveKit

For development, `docker compose up -d livekit` starts it in `--dev` mode (key `devkey`, secret
`secret`, matching the defaults above); see [Local development](local-development-and-docker.md). Or
run `brew install livekit`, then `livekit-server --dev --bind 0.0.0.0`.

For a real server:

- Give LiveKit its own key and secret, set `rtc.use_external_ip: true` or `--node-ip`, and keep port
  7880 private.
- Open **UDP 7882** (media) and **TCP 7881** (fallback) to the internet.

Packaging Raindrop and LiveKit together in one Docker deployment is planned.

## Seams for plugins

| Seam | Default | Registered with |
|---|---|---|
| `IVoiceTransport` | `UnavailableVoiceTransport`, or `LiveKitVoiceTransport` via `AddRaindropLiveKit` | `TryAddSingleton`; register yours **before** `AddRaindropServer` |
| `IVoiceSignalRelay` | `LiveKitSignalRelay` | `TryAddEnumerable`; optional, only for transports whose clients connect through Raindrop |
| `IVoiceStateStore` | `InMemoryVoiceStateStore` | `TryAddSingleton` |
| `IVoiceMuteStore` | `RelationalVoiceMuteStore`, from the database driver | `IDatabaseDriver.CreateVoiceMuteStore` |
| `IVoiceJoinPolicy` | none | `TryAddEnumerable`; each one can refuse a join by throwing an `ApiException` |
| `voice.*` events | `VoiceEvents` | ordinary `IServerEventHandler<VoiceParticipant>` / `<VoiceLeftEvent>` subscribers |

## Related

- [Channels](channels.md): `IChannelModule` and the channel lifecycle
- [Events](events.md): `voice.state` / `voice.left`
- [Roles & permissions](roles-and-permissions.md): `connect`, `speak`, `mute_members`, rollouts
- [Configuration](configuration.md): the `voice` section
