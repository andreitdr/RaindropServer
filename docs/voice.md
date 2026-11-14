# Voice channels

A voice channel is a group call: members join and leave whenever they like, and everyone in it hears
everyone else. Members can mute themselves, deafen themselves (stop hearing anyone, which also mutes),
and a moderator can **mute somebody for everyone**. Per-member volume and "mute just for me" are
client-side and never reach the server.

Code: `RaindropServer/Channels/Voice/` (transport-agnostic), `RaindropServer.Voice.LiveKit/` (the
media transport), `raindrop.v1.VoiceService` ([`voice.proto`](../../RaindropProto/raindrop/v1/voice.proto),
served by [`VoiceGrpcService`](../RaindropServer.Grpc/Services/CommunityServices.cs)) for call control,
and [`VoiceSignalRoutes`](../RaindropServer/Channels/Voice/VoiceSignalRoutes.cs) for the signalling relay.

## Who does what

Raindrop is the **authority**; a media server only **carries media**.

| Raindrop decides | The media server does |
|---|---|
| who may see, join, speak, show their camera and share their screen in a channel (roles and permissions) | receive each participant's microphone, camera and screen share |
| who is in which call, and their mute/deafen/camera/share state | forward them to everybody else in the room (an SFU) |
| moderator mutes, stored so they survive restarts | refuse any source its grant does not allow |

The media server shipped with Raindrop is **LiveKit**, running as a sidecar next to the server. It
sits behind the `IVoiceTransport` seam, so nothing in `VoiceService` knows which one it is talking to.
The join response names the transport (`transport: "livekit"`), and a client picks its engine by
that name.

```
client ──gRPC: calls + event stream (pinned TLS)───▶ Raindrop ──Twirp protobuf (private HTTP)──▶ LiveKit
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
| `Video` | `video` | Be seen. Without it the participant is `camera_suppressed` and LiveKit refuses their camera. Separate from `speak`, so a member can be heard without being seen. |
| `ScreenShare` | `screen_share` | Share a screen or a window. Without it the participant is `screen_share_suppressed` and LiveKit refuses their share. Separate from `video`, so an owner can let members be seen without letting them put a film in front of the whole call. A window's sound additionally needs them to be able to speak. |
| `MuteMembers` | `mute_members` | Mute or unmute somebody for everyone. Checked **server-wide**, never per channel, and not part of the channel-creator rule, because a mute follows its target into every call. |

`connect`, `speak`, `video` and `screen_share` are in the `everyone` role by default. An existing server gains them
once through a **permission rollout**; see below.

Permissions are enforced by the media server, not just the UI:

- Everything a participant may send is one `VoicePublishGrant` — microphone, camera, screen, the
  screen's sound — derived from their record (`VoiceParticipantRecord.Publish`) and handed to the
  transport whole, because LiveKit replaces a participant's permission object rather than merging.
- A token's `canPublish` is "any of those". `canPublishSources` names exactly the granted ones
  (`microphone`, `camera`, `screen_share`, `screen_share_audio`), and **never nothing** — LiveKit
  reads an empty list as "every source", so `canPublish: false` is what actually says no
  (`LiveKitJwt.PublishableSources`, used by the join token and by `UpdateParticipant` alike).
  `screen_share_audio` is never named without `screen_share`.
- A change mid-call is pushed with LiveKit's `UpdateParticipant`, and LiveKit withdraws any track
  whose source the new grant no longer names. Losing `view_channel` or `connect` mid-call removes the
  participant with reason `permission_lost`; losing `video` turns their camera off, and losing
  `screen_share` ends their share, each without disturbing the rest of their call.

**Nobody may mute an administrator for everyone unless they are one.** That is the same spirit as "you
cannot hand out what you do not hold". It can never lock anybody out, because an administrator can
always unmute themselves.

### Permission rollouts

`RoleOptions.DefaultMemberPermissions` only seeds a **new** server, so an existing server upgraded to
a build with voice would otherwise have nobody but its administrators able to join a call.
`PermissionRollouts.All` (`Roles/PermissionRollouts.cs`) fixes that. It lists permissions a build
introduces for everybody: `new("voice", Connect | Speak)`, `new("voice-video", Video)` for the
build that added camera video, and `new("voice-screen-share", ScreenShare)` for the one that added
screen sharing.

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
  full / moved-from / replaced; `SetCameraAsync(on, cameraCapacity)` returns full / not-allowed;
  `SetScreenShareAsync(on, kind, audio, shareCapacity)` returns full / not-allowed / muted). That
  enforces capacity, both caps and **one call per member per server** atomically — two members
  racing for the last place, the last camera place or the last sharing place cannot both win.
- Each record keeps the **session token** that joined. Logging out on one device ends that device's
  call and no other.

**Mutes for everyone** are stored in `raindrop_voice_mutes` (`IVoiceMuteStore`, one row per muted
member). They survive restarts and rejoins, and can be lifted while the member is not in a call.

## Camera video

Advertised as the capability `voice.video.v1`, when a media server is configured **and**
`voice.maximumCameraPublishers` is above zero. A new capability name rather than a protocol version
bump, which is how every additive feature is announced: a client too old to know it shows no camera
button.

**The camera grant follows the camera being on, not the permission.** That is the whole design, and
it is what makes the cap real:

1. A member joins. Their token names `microphone` only, whatever their permissions say —
   `VoiceGrant.CanPublishCamera` is false at join, because joining never starts a camera.
2. They call `SetVoiceSelfState(camera_on: true)`. The server checks `video`, counts the cameras
   already on in that call, and either refuses (`missing_permission` / `camera_limit_reached`) or
   records it.
3. Having recorded it, the server pushes the new grant to LiveKit through `UpdateParticipant`, which
   adds `camera` to `canPublishSources`. The client waits for LiveKit's permission update and *then*
   publishes.
4. Turning the camera off reverses it, and a client stops publishing **before** telling the server, so
   the grant is never withdrawn from under a live publication.

So a client that ignores a refusal still cannot send: LiveKit was never told to accept its camera.
`VoiceParticipantRecord.CanPublishCamera` is `CameraOn && !CameraSuppressed` — note that unlike
`CanSpeak` it requires the member to have *asked*, which is the mechanism in one line.

**Losing `video` mid-call** turns the camera off as well as forbidding it (`ReevaluateAsync`), so the
record never claims a camera nobody is allowed to see, and the grant goes back to microphone-only.

**The cap** (`voice.maximumCameraPublishers`, 6 by default) is per call, not per server, and counted
inside `IVoiceStateStore.SetCameraAsync` so two members racing for the last place cannot both win. It
is deliberately far below `maximumParticipants`: every camera is sent to everybody else, so six
cameras in a call of 25 is already 150 video streams out of the media server. Zero switches camera
video off entirely, and the capability with it.

## Screen sharing

Advertised as the capability `voice.screen.v1`, when a media server is configured **and**
`voice.maximumScreenShares` is above zero — separate from `voice.video.v1`, because an owner can
switch either off without the other.

A share is **a whole screen** or **one window** (`ScreenShareKind`, a closed set, so an enum on the
wire and the names `screen` / `window` in the domain). A window may also carry **its own sound**; a
screen never does, because the host's sound is never sent. The server cannot see what a client
captures — the media is end-to-end encrypted — so that rule is enforced the only way it can be, as a
*grant*: `screen_share_audio` is only ever added to a participant's grant for a window.

It works exactly like the camera, and for the same reason:

1. Joining grants no share. `SetVoiceSelfState(screen_share_on: true, screen_share_kind, screen_share_audio)`
   checks `screen_share`, counts the shares already running in that call inside
   `IVoiceStateStore.SetScreenShareAsync`, and either refuses (`missing_permission` /
   `screen_share_limit_reached`) or records it.
2. Having recorded it, the server pushes the grant to LiveKit, which adds `screen_share` — and
   `screen_share_audio` for a window with sound — to `canPublishSources`. The client waits for that
   grant and then publishes.
3. Stopping reverses it; the client unpublishes first.

`VoiceParticipantRecord.CanPublishScreen` is `ScreenShareOn && !ScreenShareSuppressed`.
`CanPublishScreenAudio` is that **and** a window **and** sound asked for **and** `CanSpeak`:

- **A window's sound follows the right to speak, not the share.** A member muted for everyone, or
  without `speak`, sends no sound into the call whatever its source. Asking for it then is refused
  (`missing_permission`, decided inside the store so a mute landing at the same moment cannot be
  overtaken), and a mute for everyone — or losing `speak` — mid-share clears `screen_share_audio`
  and withdraws just that source, leaving the picture. Lifting the mute does not bring the sound back:
  LiveKit has withdrawn the track, and only the sharer can start it again.
- **Their own mute does not stop it.** The window's sound is its own track, separate from their
  microphone, which is the point of it being one.

**The request is the whole state.** `SetVoiceSelfState` carries mute, deafen, camera *and* share, and
every field is read: a client that leaves the share out of a mute toggle ends it. A share that is
already running arrives again with every toggle and is only touched when something about it differs;
a running share that changes what it carries (sound switched off because it could not be captured)
keeps the place it has and is never told the call is full. A share with no kind, or a screen with
sound, is `invalid_payload`, and none of that request is applied.

**The cap** (`voice.maximumScreenShares`, 2 by default) is per call. Zero switches screen sharing
off, and the capability with it. Losing `screen_share` mid-call ends a running share
(`ReevaluateAsync`). What one share costs depends on how good it may be, which is the next thing.

### How good a share may be

`voice.maximumScreenShareHeight` (480, 720 or 1080; 1080 by default) and
`voice.maximumScreenShareFrameRate` (15, 24, 30 or 60; 60 by default) are the best a shared screen may
be sent at on this server. Closed sets rather than ranges: a client sends a share in fixed sizes and
rates, and a ceiling between two of them is one nothing can aim at, so anything else is a startup
error naming the values that are taken. A sharer picks their own maximum up to this ceiling, or leaves
it automatic, which aims for 1080p at 30.

**The ceiling is honoured by clients, not enforced here**, and that is a limit of the design rather
than something left undone. A call's media is end-to-end encrypted and travels through the media
server, never through Raindrop; there is no frame for this server to measure and nothing it could
refuse. So the ceiling is *advertised* — `GetVoiceStatus.maximum_screen_share_height` and
`.maximum_screen_share_frame_rate`, both 0 when this server carries no screen sharing — and a Raindrop
client holds its own share to it, greying out what it rules out. It is the same kind of promise the
1080p box has always been. What it buys an owner is bandwidth on the media server.

**What a share costs** at each ceiling, as the most its largest version may spend
(`VoiceOptions.ScreenShareBitrate`, kept equal to the client's `shareTopBitrate`), in kbit/s:

| | 15 | 24 | 30 | 60 |
|---|---|---|---|---|
| 480p | 500 | 700 | 800 | 1200 |
| 720p | 1200 | 1600 | 2000 | 3000 |
| 1080p | 2500 | 3200 | 4000 | 6000 |

A watcher receives at most that (clients only receive a share once their user chooses to watch it,
and a watcher on a slower connection is sent one of two smaller versions the sharer also sends). The
sharer sends that plus about half as much again for those smaller versions. These are ceilings: a
screen that is not changing costs a small fraction of them. The startup warning for more than three
shares at once quotes the figure for the ceiling this server actually allows.

Choosing what to share, how a share is sized and sent, the versions it is sent in, and which platforms
can capture a window's sound are all the client's: see `RaindropClient/docs/voice.md#screen-sharing`.

## Keeping Raindrop and LiveKit in step

There are no webhooks. LiveKit's webhooks would have to call into Raindrop's self-signed HTTPS, which
LiveKit does not trust without extra setup. Instead:

- **Join and leave are gRPC calls**, so the roster changes the instant somebody clicks. A joined member
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

  **"Already gone" is success.** LiveKit answers a call about a room from the node holding it, so
  removing or muting somebody at the moment they were leaving anyway comes back either `not_found`
  (404) or `unavailable` (503, `no response from servers` — it waited ~3 s for a node that had closed).
  Both mean the state asked for has arrived, so both are treated as done rather than reported. A call
  that names no room, such as listing the rooms, is not: nothing answering there means LiveKit itself
  is in trouble, and a reconciler told "no rooms" would tear down every live call.
- **A dead client leaves in seconds.** A crashed or killed client cannot call `LeaveVoice`, but the
  OS closes its sockets, and the signalling relay sees its LiveKit connection end. The relay reports
  every connection opening and closing to `IVoiceSignalMonitor` (implemented by `VoiceService`). When a
  member's last signalling connection in a call has stayed closed for `SignalLossGrace` (5 s), they
  are dropped (`disconnected`): `voice.left` goes to everybody and LiveKit is told to remove them.
  - **A network blip is not a crash.** LiveKit's SDK opens a new signalling connection within a second
    or two when it resumes, which cancels the removal.
  - **Only the join whose connection closed is removed.** An app relaunched after a crash can come
    back on the same session token, so the join time (`JoinedAtUtc`) is what distinguishes the old
    join from a new one.
  - **A clean leave is unaffected.** `LeaveVoice` has already removed the member, so the check finds
    nothing.
  - **The reconciler is still the fallback.** If a transport has no relay, or the relay misses a
    connection, the `JoinGrace` sweep above still drops the member.
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
- **A liveness signal.** Because every signalling connection passes through it, the relay is what
  notices a client disappear. It tells `IVoiceSignalMonitor` when each connection opens and closes;
  see "A dead client leaves in seconds" above.
- **The one route that is not gRPC.** It carries LiveKit's own protocol (a WebSocket with LiveKit's
  protobuf inside), which its client SDK speaks and Raindrop forwards unread. `VoiceSignalRoutes` maps
  `/api/channels/voice/signal/{**path}` only when a transport registers an `IVoiceSignalRelay`, and
  none of Raindrop's own authentication runs there: the caller authenticates with LiveKit's token, not
  a Raindrop session. The route answers GET and CONNECT, so a WebSocket works over HTTP/1.1 (what
  dart:io uses) or HTTP/2.

## End-to-end encrypted calls

Media is encrypted twice:

- **DTLS-SRTP**, as far as LiveKit.
- **End to end, inside that.** Each client's LiveKit frame cryptor encrypts every frame (AES-GCM)
  before it leaves the device — voice and camera alike. LiveKit forwards frames it cannot decrypt, so
  neither the media server nor whoever runs it can hear or see a call.

**Where the key comes from.** The key comes from the voice channel's MLS group, which is the same
[delivery service](e2ee.md#the-delivery-service) as text channels. Its members are the devices of
everyone with `view_channel` and `connect`. A client exports it as
`MLS-Exporter("raindrop/voice/v1", group id, 32)` for the group's current epoch, and puts it in key
slot `epoch mod 16`, so the previous keys still decrypt frames in flight. Keys are never ratcheted, and
a decryption failure never turns encryption off.

**One key per call, covering every track.** There is no second label for camera video, and there could
not usefully be one: LiveKit's shared-key provider holds one key per ring slot for the whole room, and
moving a participant to a new slot moves every one of their tracks — microphone and camera together.
So camera video needed no change to the crypto at all.

**Key changes.** The key changes whenever the group's epoch does:
- somebody gains or loses access to the channel
- a device is revoked, and members reconcile it out
- every 10 minutes during a call, when the participant with the lowest member id rotates it

**The server's part** is small, and it holds no key:
- It advertises `e2ee.voice.v1`, which tells clients every call here is encrypted, so a client that
  cannot get the key refuses to join rather than joining in the clear.
- Its LiveKit tokens grant **no data channel** (`canPublishData = false`), so nothing that could carry
  key material passes through LiveKit.

**Clients fail closed.** Media published without encryption, or not decrypting with the group's key,
is never played or drawn: audio is kept silent, and a camera is not rendered and not even subscribed
to. The call bar shows the state as a lock. Clients also refuse to *send* a camera the frame cryptor
did not take, and pin the video codec for that reason — see the client's
[`docs/voice.md`](../../RaindropClient/docs/voice.md#codecs-and-the-frame-cryptor).

**What the server can still see** is the call's metadata: who is in which call, when, speaking
indicators, mute state, and whether a camera is on.

**A participant who leaves a call keeps the key** only as long as they stay in the channel's group,
which means they could still join the call anyway. Removing their access removes them from the group
and moves the key on.

## Voice API

All authenticated. `raindrop.v1.VoiceService`:

| RPC | Purpose |
|---|---|
| `GetVoiceStatus` | → `{available, transport, maximum_participants, maximum_camera_publishers, maximum_screen_shares, maximum_screen_share_height, maximum_screen_share_frame_rate, participants}`: whether voice works here, how many cameras and shares a call carries, the best a share may be sent at (clients hold themselves to it), and everybody in every call the caller can see |
| `JoinVoice` | `{channel_id, self_mute, self_deaf}` → `{channel_id, transport, url?, path?, token, expires_at, you, participants}`. Needs `connect`. Joining another call leaves the first; joining the same call from another device takes it over. |
| `LeaveVoice` | `{channel_id}` → `{left}`. Idempotent, and only the session that joined can leave, so a late leave from one device cannot end another device's call. |
| `SetVoiceSelfState` | `{self_mute, self_deaf, camera_on, screen_share_on, screen_share_kind, screen_share_audio}` → `{participant}`. **The whole state**: a field left out is read as off. Deafening always mutes too. Mute and deafen are only icons and are always accepted; **the camera and a share are decisions and can be refused** (`missing_permission`, `camera_limit_reached`, `screen_share_limit_reached`), and a refused one leaves a mute in the same request standing. A malformed share (`invalid_payload`) is refused whole. |
| `MuteVoiceMember` | `{user_id, muted}` → `{user_id, muted, changed}`. Mute or unmute for everyone; needs server-wide `mute_members`. |

The signalling relay at `/api/channels/voice/signal/{path}` (see above) is not part of this service and
not a Raindrop-session route.

`VoiceParticipant` is `{channel_id, user_id, self_mute, self_deaf, server_mute, suppressed, connected,
joined_at, camera_on, camera_suppressed, screen_share_on, screen_share_kind, screen_share_audio,
screen_share_suppressed}`: the whole state, merged by clients on
`(channel_id, user_id)`. `url` is absent in this version (everything goes through the relay). It is in
the contract so a transport can point clients at a media server with its own TLS later.

Error codes on top of the channel ones:

| Code | Status | When |
|---|---|---|
| `voice_unavailable` | 503 | No media server is configured (`voice.provider` is null) |
| `channel_full` | 409 | The call already holds `maximumParticipants` other members |
| `camera_limit_reached` | 409 | The call already has `maximumCameraPublishers` other cameras on |
| `screen_share_limit_reached` | 409 | The call already has `maximumScreenShares` other shares running |
| `invalid_payload` | 400 | A share with no kind, or a screen share asking for sound |
| `not_in_voice` | 409 | `state` from a session that is not in a call |
| `member_not_found` | 404 | `mute` for somebody who is not an active member |
| `missing_permission` | 403 | No `connect`, no `video` (turning a camera on), no `screen_share` (starting a share), asking for a window's sound while unable to speak, no `mute_members`, or muting an administrator without being one |
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
  "maximumCameraPublishers": 6,  // cameras on at once in one call; 0 switches camera video off
  "maximumScreenShares": 2,      // shares at once in one call; 0 switches screen sharing off
  "maximumScreenShareHeight": 1080,    // 480 | 720 | 1080 — the best a share may be sent at
  "maximumScreenShareFrameRate": 60,   // 15 | 24 | 30 | 60
  "startupCheck": "warn",        // off | warn | require — how hard to look for LiveKit at startup
  "livekit": {
    "apiUrl": "http://localhost:7880",
    "apiKey": "raindrop",        // whatever LiveKit itself was configured with
    "apiSecret": null,           // 32+ random characters; prefer the environment variable
    "allowWeakSecret": false
  }
}
```

Environment overrides: `RAINDROP_VOICE_PROVIDER`, `RAINDROP_VOICE_STARTUP_CHECK`,
`RAINDROP_LIVEKIT_API_URL`, `RAINDROP_LIVEKIT_API_KEY`, `RAINDROP_LIVEKIT_API_SECRET`,
`RAINDROP_LIVEKIT_ALLOW_WEAK_SECRET`. Prefer the environment variable to writing the secret into the
file.

With `provider: null`, voice channels can still be created and deleted, but joining is refused with
`voice_unavailable`, and `GetVoiceStatus` reports `available: false`. `livekit` with a
missing key or secret is a startup error. The console's `voice` command lists who is in which call.

**There is no default secret to forget to change.** Nothing here ships with one, and LiveKit's own
`--dev` values are among those refused outright (see the table below). Generate the secret instead:

```bash
openssl rand -base64 48
```

`allowWeakSecret` (or `RAINDROP_LIVEKIT_ALLOW_WEAK_SECRET=true`) turns those refusals into a warning.
It is an escape hatch for a throwaway LiveKit on your own machine, nothing more; a server anybody else
connects to has no reason to set it, because the secret signs every join token.

`apiUrl` is where *this server* reaches LiveKit, and stays private. Plain `http://` is accepted only
for a loopback address or a single-label container name such as `http://livekit:7880`; anything
further away must be `https://`. See [Transport security](transport-security.md#livekit).

### What is checked at startup

Voice is the part of the configuration with the most ways to be quietly wrong — a secret that does not
match LiveKit's, a port nothing listens on, the WebSocket address copied in place of the API one — and
every one of them leaves a server that starts, runs, and cannot carry a single call. So it is checked
twice before the server listens, and the whole voice section is validated even when several things are
wrong at once (see [Configuration](configuration.md#startup-validation)).

**By reading the settings** (`LiveKitOptions.Validate`, and `VoiceOptions.Validate` for the rules that
hold whichever media server carries the calls):

| Refused | Why |
|---|---|
| `provider` naming a media server this build does not have | listed with what it does have, and what it was probably meant to be |
| a `ws://` or `wss://` `apiUrl` | that is LiveKit's *client* address; this setting wants its HTTP API |
| an `apiUrl` with a path, query or credentials in it | Raindrop appends LiveKit's own API paths to it |
| plain `http://` to anything but loopback or a container name | join tokens would cross the network readable |
| a missing, quoted, whitespace-wrapped or control-character `apiKey`/`apiSecret` | how an `.env` value arrives mangled |
| an `apiSecret` under 32 characters, well-known, a placeholder (`${…}`, `<your secret>`, `change…`), or repeating fewer than 8 distinct characters | anybody who knows it can mint a token into any call |
| an `apiSecret` equal to the `apiKey` | the key names the server; the secret proves it |
| a token lifetime over an hour | LiveKit cannot revoke a token, so that is how long a removed member could reconnect |
| `maximumParticipants` outside 2–1000 | warned above 50, where a full call's bandwidth starts to hurt |

An `apiUrl` with no port, and a `livekit` section filled in while `provider` is null, are warnings: the
server runs, but not the way its owner probably meant.

**By going and looking** (`LiveKitStartupCheck`, an `IStartupCheck` the module registers): the host
resolves, something answers, it is LiveKit's API, it accepts *this* key and secret, and its clock is
within 30 seconds of this server's — tokens carry the time they were minted, so two machines that
disagree make tokens that expire before they are used.

`startupCheck` decides how much an unreachable LiveKit matters, because a media sidecar and the server
that uses it are usually started together:

| `startupCheck` | A LiveKit that cannot be reached | A LiveKit that refuses these credentials, or is not LiveKit |
|---|---|---|
| `off` | not checked | not checked |
| `warn` (default) | warning; the server starts and calls fail until LiveKit is up | **error; the server does not start** |
| `require` | error; the server does not start | error; the server does not start |

Waiting cannot fix a secret that does not match, which is why no setting makes that one survivable.

### Running LiveKit

`docker compose up -d livekit` runs `livekit/livekit-server` with the settings in
[`livekit.yaml`](../livekit.yaml) and the key pair from `.env` — **not** `--dev`.

The image is **pinned** (`v1.13.7` today), not `latest`. Raindrop drives LiveKit's room API (listing
rooms and participants, updating permissions, removing participants, deleting rooms) as Twirp in its
binary protobuf form. It uses a trimmed copy of LiveKit's own RoomService messages,
[`RaindropServer.Voice.LiveKit/Protos/livekit_room_service.proto`](../RaindropServer.Voice.LiveKit/Protos/livekit_room_service.proto),
taken from the `livekit/protocol` commit that release is built against. LiveKit never renumbers a
field, so a newer image keeps working, but move the pin and that file together. The only JSON that
crosses that link is a Twirp error, which the Twirp spec always writes as JSON. `LiveKitRoomClient`
reads its `code` and `msg`.

Set it up once:

```bash
cp .env.example .env
openssl rand -base64 48
```

Put the generated value in `.env`, and give Raindrop the *same* pair:

```dotenv
LIVEKIT_API_KEY=raindrop
LIVEKIT_API_SECRET=<the generated secret>
LIVEKIT_NODE_IP=127.0.0.1       # this machine's LAN address when other devices join calls
```

```bash
RAINDROP_VOICE_PROVIDER=livekit \
RAINDROP_LIVEKIT_API_KEY=raindrop \
RAINDROP_LIVEKIT_API_SECRET=<the same secret> \
dotnet run --project ServerCli
```

The same values can live in `raindrop.json` under `voice.livekit` instead, but a secret in a file is a
secret in a backup. See [Local development](local-development-and-docker.md#livekit-for-voice-channels).

Without Docker, `brew install livekit` and then run it with the same key pair:

```bash
LIVEKIT_KEYS="raindrop: <the same secret>" livekit-server --bind 0.0.0.0 --node-ip 127.0.0.1
```

`livekit-server --dev` is quicker to type, but its key pair is `devkey` / `secret`, which Raindrop
refuses unless you also set `allowWeakSecret`.

For a real server:

- Give LiveKit its own key and secret — one per deployment, never shared or reused — set
  `rtc.use_external_ip: true` or `--node-ip`, and keep port 7880 private.
- Open **UDP 7882** (media) and **TCP 7881** (fallback) to the internet.

Raindrop and LiveKit are already packaged together for Docker: `docker-compose.yml` runs a
`livekit` service from [`livekit.yaml`](../livekit.yaml) and the `server` service depends on it, and
[`deploy.sh`](deployment.md) carries both to a host. See
[Local development & Docker](local-development-and-docker.md#livekit-for-voice-channels).

### When a participant joins and is removed without a connection

LiveKit logging `starting RTC session` and then `removing participant without connection` for the
same member, over and over, means the signalling reached it and the media never did. The relay is
rarely the culprit — it either forwards the whole stream or fails the WebSocket outright, and the
`participantInit` it logs proves the join arrived intact. Read the candidate lists in the
`removing participant without connection` line instead; they say which side ran out of ways to
connect:

- **No `[remote]` candidates at all.** The client never got as far as gathering. Its own logs are
  the place to look.
- **`[remote]` candidates, but every one of them `tcp … :9`.** Those are ICE-TCP *active*
  placeholders. A client offering nothing but those has no UDP transport, which on a sandboxed macOS
  build means the app is missing `com.apple.security.network.server` — see the client's
  [Voice doc](../../RaindropClient/docs/voice.md#platform-setup).
- **`[local]` candidates naming an address the client cannot reach** — `127.0.0.1` when the client
  is on another machine, or a container address. That is `--node-ip` / `LIVEKIT_NODE_IP`, above.

The two shapes of session are worth telling apart, because they have different causes. One that ends
in ~100 ms with `reason: SIGNAL_SOURCE_CLOSE` is the client tearing its own socket down, normally to
start a full reconnect after its peer connection failed immediately; one that ends after ~10 s with
`reason: CLIENT_REQUEST_LEAVE` is the LiveKit SDK giving up on the peer connection and leaving. A
client that cannot reach the media ports at all produces the second repeatedly, with the first in
front of each attempt.

## Seams for plugins

| Seam | Default | Registered with |
|---|---|---|
| `IVoiceTransport` | `UnavailableVoiceTransport`, or `LiveKitVoiceTransport` via `AddRaindropLiveKit` | `TryAddSingleton`; register yours **before** `AddRaindropServer` |
| `IVoiceSignalRelay` | `LiveKitSignalRelay` | `TryAddEnumerable`; optional, only for transports whose clients connect through Raindrop |
| `IVoiceSignalMonitor` | `VoiceService` | `TryAddSingleton`; a relay reports signalling connections opening and closing to it, which is how a crashed client leaves within `SignalLossGrace` |
| `IVoiceStateStore` | `InMemoryVoiceStateStore` | `TryAddSingleton` |
| `IVoiceMuteStore` | `RelationalVoiceMuteStore`, from the database driver | `IDatabaseDriver.CreateVoiceMuteStore` |
| `IVoiceJoinPolicy` | none | `TryAddEnumerable`; each one can refuse a join by throwing an `ApiException` |
| `IStartupCheck` | `LiveKitStartupCheck`, when LiveKit is the transport | `AddStartupCheck<T>()`; how a transport confirms its settings describe something real before the server listens |
| `voice.*` events | `VoiceEvents` | ordinary `IServerEventHandler<VoiceParticipant>` / `<VoiceLeftEvent>` subscribers |

## Related

- [Channels](channels.md): `IChannelModule` and the channel lifecycle
- [Events](events.md): `voice.state` / `voice.left`
- [Roles & permissions](roles-and-permissions.md): `connect`, `speak`, `mute_members`, rollouts
- [Configuration](configuration.md): the `voice` section
