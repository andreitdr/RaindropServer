# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Documentation wiki

[`docs/`](docs/README.md) is a per-feature documentation wiki for this project, meant to stay current.
**Whenever you change a feature or add a new one — a new RPC, a changed contract, a new
permission, a new database driver, a new config option — update the matching file(s) under `docs/` as
part of that same change**, not as a follow-up. If nothing existing fits, add a new file and link it
from `docs/README.md`.

## Commands

The wire protocol lives in the sibling [RaindropProto](../RaindropProto/CLAUDE.md) repo, which must be
checked out next to this one (`../RaindropProto`): `RaindropServer.Grpc` compiles its `.proto` files on
every build, and the build stops with a message saying so when they are missing.

Build and run (from repo root):

```bash
dotnet build
```

```bash
dotnet run --project ServerCli
```

Tests are in `RaindropServer.Tests` (xUnit):

```bash
dotnet test
```

They cover:
- the whole server in memory, called through a real gRPC client (`GrpcTests.cs`, over ASP.NET Core's
  `TestServer`): who may call what, how a refusal arrives (status + `ErrorInfo.reason`), the event
  stream (ready first, pushed events, resubscribe, presence, `session_ended`, the heartbeat timeout,
  plugin events), and streamed uploads/downloads (frames straddling chunks, resume, offsets)
- the MLS envelope parser and device certificates, against real fixtures from the Rust core
  (`RaindropServer.Tests/Fixtures/*.hex`)
- Ed25519 (RFC 8032 vectors) and the server identity key
- the stores under real concurrency: racing commits for one epoch, racing key-package claims, history
  paging across plaintext and encrypted messages
- the delivery-service and device-service rules
- attachments: the ciphertext-length contract with the Rust core, the store's claim races, the upload /
  attach / download rules over a real folder, and the v2 attestation bytes against a golden vector
- configuration: what `ServerConfig` makes of a mistyped `raindrop.json`, every component's own
  option validation (TLS cannot be switched off), and `LiveKitStartupCheck` against a stub LiveKit that
  answers however the test says
- the LiveKit room client speaking binary Twirp both ways, and reading Twirp's JSON errors

Store tests run on SQLite, and also on PostgreSQL and SQL Server when `RAINDROP_TEST_POSTGRES` or
`RAINDROP_TEST_SQLSERVER` holds a connection string (the docker-compose services will do). End-to-end
tests against a running server, with the real Rust core, are in the client:
`RaindropClient/test/integration/`.

Once the CLI is running, exercise it with `grpcurl` (`brew install grpcurl`). Everything is gRPC; the
server presents a self-signed certificate by default, so `-insecure` is needed until you trust it, and
the protos come from `../RaindropProto` because the server serves no reflection:

```bash
grpcurl -insecure -import-path ../RaindropProto -proto raindrop/v1/server.proto localhost:5001 raindrop.v1.ServerService/GetServerInfo
```

```bash
grpcurl -insecure -import-path ../RaindropProto -proto raindrop/v1/users.proto -d '{"user_id":"<uuid>","username":"alice","password":"correct horse"}' localhost:5001 raindrop.v1.UserService/Register
```

Authenticated calls carry the token that came back from registering or logging in:

```bash
grpcurl -insecure -import-path ../RaindropProto -proto raindrop/v1/members.proto -H "authorization: Bearer <token>" localhost:5001 raindrop.v1.MemberService/ListMembers
```

```bash
grpcurl -insecure -import-path ../RaindropProto -proto raindrop/v1/channels.proto -H "authorization: Bearer <token>" -d '{"type":"text","name":"general"}' localhost:5001 raindrop.v1.ChannelService/CreateChannel
```

The first UUID to register owns the server, so its token is the one that can make roles:

```bash
grpcurl -insecure -import-path ../RaindropProto -proto raindrop/v1/roles.proto -H "authorization: Bearer <owner token>" -d '{"name":"Moderator","permissions":["manage_messages","rename_channels"]}' localhost:5001 raindrop.v1.RoleService/CreateRole
```

The event stream is bidirectional; `-d @` reads its frames from stdin:

```bash
(echo '{"heartbeat":{}}'; sleep 30) | grpcurl -insecure -import-path ../RaindropProto -proto raindrop/v1/events.proto -H "authorization: Bearer <token>" -d @ localhost:5001 raindrop.v1.EventService/Connect
```

`ServerCli probe https://localhost:5001` prints a running server's handshake as `key=value` lines.

In the running CLI's console, `drivers` lists the installed database modules, `db` shows the active
one, `fingerprint` prints the TLS fingerprint for clients to pin, `roles` lists the server's roles
and what each grants, `grant <user> <role>` and `revoke <user> <role>` change who holds one,
`events` shows how many clients have the event stream open, `voice` lists who is in which voice call,
`purge-plaintext` deletes every pre-encryption message and the deleted-message archive (after a `yes`),
`reports` shows the latest reported messages, `attachments` shows how many encrypted files are stored and
how much room they take,
and `quit` shuts the server down gracefully. With no console attached (a container started
without a TTY) the server keeps serving until it receives SIGINT/SIGTERM.

Shutdown is bounded by `ServerHostOptions.ShutdownTimeout`, five seconds by default rather than the
host's usual thirty. A connection still draining must not outlast the ten seconds `docker stop`
allows before it resorts to SIGKILL — a server killed mid-stop is one that never ran its graceful
shutdown at all.

Docker: `docker-compose.yml`'s `server` service builds and runs the whole stack (server + Postgres +
LiveKit) — `docker compose up -d server`; it passes `../RaindropProto` as the named build context
`raindrop_proto` (Compose 2.17+). Built directly, `ServerCli/Dockerfile` is built from the repo root as
context since it references `../.dockerignore`, with the protocol as a second context:

```bash
docker build -f ServerCli/Dockerfile --build-context raindrop_proto=../RaindropProto -t raindrop-server .
```

Deploying to the owner's own Docker server is `./deploy.sh` (reference: [`docs/deployment.md`](docs/deployment.md)):
it exports the committed `HEAD` (or `--ref`) with `git archive`, and RaindropProto's (`--proto-ref`)
beside it, builds the image on the server, restarts `server`, and checks — with `ServerCli probe` run
inside the container — that the server's identity key did not change. `--dry-run` changes nothing, and
deploying an older ref is the rollback. State (`data/`, `.env`) lives outside the build context on the
server and is never touched.

## Architecture

A solution of eight projects targeting `net10.0` (the eighth, `RaindropServer.Tests`, is the xUnit
suite), built against the sibling RaindropProto checkout:

- **RaindropServer** (`RaindropServer/`) — the library: hosts an ASP.NET Core app on a background
  thread and owns the domain layer (users, sessions, password hashing, the member roster and presence,
  roles and permissions, channels and their messages, push events, the database abstraction). **It
  knows no transport** — no gRPC or protobuf package — and maps no route of its own except the voice
  signalling relay's (`VoiceSignalRoutes`).
- **RaindropServer.Grpc** (`RaindropServer.Grpc/`) — the transport: compiles
  `../RaindropProto/raindrop/v1/*.proto` (`Grpc.Tools`, C# into `obj/`, never committed), implements every
  `raindrop.v1` service as a thin layer over the library (`Services/`, `Mapping/`), and holds
  `RaindropInterceptor` (sessions, error statuses) and the event stream (`Events/`).
- **RaindropServer.Data.Sqlite / .Postgres / .SqlServer** — the database modules. Each is a separate
  assembly holding one `IDatabaseDriver` and the client library for that database.
- **RaindropServer.Voice.LiveKit** — the media module: carries voice calls on a LiveKit sidecar
  (tokens, the room API, the signalling relay) behind `IVoiceTransport`. No LiveKit SDK — the JWTs are
  hand-written, and the room API is binary Twirp over LiveKit's RoomService messages, vendored in
  `Protos/` from the protocol version the pinned `livekit-server` image is built against. Idle unless
  `voice.provider` names it.
- **ServerCli** (`ServerCli/`) — the executable everyone runs. It reads `raindrop.json`, enables one
  database module, registers the domain (`AddRaindropServer`) and the gRPC transport
  (`AddRaindropGrpc`), starts the server, and hosts the owner's console. `ServerCli probe <url>` asks a
  running server for its handshake instead.

Raindrop is decentralised: every server is independent, and a client holds a **separate GUID per
server**. Nothing in here may assume another server exists.

Every server starts out identical because every owner runs this same CLI — what makes one server
different from another is its address and its users, and implicitly its database and its plugins.
The protocol every server speaks lives outside this repo, in RaindropProto, shared with the client so
the two cannot disagree about a field. Rules belong in the library; `RaindropServer.Grpc` only
translates between that protocol and the library's own records.

### gRPC services

Full reference: [`docs/grpc-services.md`](docs/grpc-services.md). Every client call is gRPC (HTTP/2,
TLS), package `raindrop.v1`, one service per domain: `ServerService`, `UserService`, `MemberService`,
`RoleService`, `ChannelService`, `TextChannelService`, `VoiceService`, `DeviceService`,
`DeliveryService`, `AttachmentService`, `EventService`. Each lives in
`RaindropServer.Grpc/Services/` and does three things: read the request (ids through `Wire.Id`), call
the library service that owns the rule, map the record it returns (`Mapping/ToWire`). The library's
contract records are not protobuf types and must stay that way.

- **Authentication**: `RaindropInterceptor` resolves `authorization: Bearer <token>` metadata through
  `ISessionStore.TryValidate` before any handler runs (`missing_token`, `invalid_session`) — for every
  call shape, streams included. A method marked `[AllowAnonymous]` skips it; six do (`GetServerInfo`
  and the five sign-in calls). Handlers read the caller with `context.Session()`.
- **Errors**: services and the library throw `ApiException` (`ApiException.Conflict("username_taken",
  …)`). Its `ApiErrorKind` picks the gRPC status (BadRequest→INVALID_ARGUMENT, Unauthorized→
  UNAUTHENTICATED, Forbidden→PERMISSION_DENIED, NotFound→NOT_FOUND, Conflict→FAILED_PRECONDITION,
  TooLarge→RESOURCE_EXHAUSTED, ServiceUnavailable→UNAVAILABLE), and its code becomes the
  `google.rpc.ErrorInfo.reason` (domain `raindrop`) in the details trailer — what clients branch on.
  Anything else is `INTERNAL`/`internal_error`, logged, never leaked. Every reason is listed in
  `../RaindropProto/docs/errors.md`; a new one goes there too.
- **Ids** arrive as strings: malformed is `invalid_payload`; empty becomes `Guid.Empty`, which the
  domain refuses with its own code exactly as it did a missing id.
- **Absent vs empty**: proto3 cannot send null, so where absent means something different the protocol
  uses `optional` or a wrapper (`PermissionSet`, `ExtensionList`, `AttestedFiles`) and the service maps
  it back to null. Keep it that way when adding fields.
- **Routes**: `ServerHost` maps every `IRouteContributor`; `AddRaindropGrpc` registers the one mapping
  the services. Messages are capped at 1 MiB (`MaximumMessageBytes`); attachment frames are 256 KiB.
- **The one route that is not gRPC** is `/api/channels/voice/signal/{path}`: LiveKit's own signalling
  WebSocket, relayed. It is why Kestrel still speaks HTTP/1.1 beside HTTP/2.

Adding an RPC: add it to the `.proto` in RaindropProto (its CLAUDE.md has the rules — own
`<Rpc>Request`/`<Rpc>Response`, never renumber), override it in the matching `*GrpcService`, map with
`Wire`/`ToWire`, add a `GrpcTests` case if it is a new shape, and update both docs folders.

### Wire format

Protobuf only — see [`docs/wire-formats.md`](docs/wire-formats.md) and RaindropProto's
`docs/conventions.md`. UUIDs are canonical lower-case strings (certificates and attestations sign ids as
text), times are `Timestamp`, crypto blobs are `bytes`, open sets (permissions, channel types, leave
reasons, capabilities) stay strings and closed ones (presence, user state, role kind, attachment kind,
upload state) are enums with an `UNSPECIFIED` zero. The only non-protobuf bytes the server handles are
LiveKit's: its signalling (relayed unread), Twirp's JSON error bodies, and its JWTs.

### Transport security

Everything is TLS, always. `ServerHost` configures Kestrel with the certificate and enables HTTP/2 (gRPC)
and HTTP/1.1 (the voice relay's WebSocket), negotiated by ALPN. There is no plain-HTTP mode:
`tls.enabled: false` and `http://` URLs are startup errors, because clients pin this server's own
certificate and speak gRPC to it — a reverse proxy in front must pass TLS through.

Raindrop servers are self-hosted on whatever address their owner has, and most will never have a
CA-issued certificate, so `ServerCertificate` generates a self-signed one on first start and saves
it. That encrypts the connection but proves nothing about who is on the other end, so **clients are
expected to pin the fingerprint**: a client already stores a per-server GUID, and stores the
server's SHA-256 fingerprint alongside it the first time it connects, refusing to continue if it
ever changes. Same trust-on-first-use model as SSH. Point `certificatePath` at a CA-issued PFX
instead and clients can verify it normally.

The fingerprint is printed at startup, available from the `fingerprint` console command, and served
by `ServerService.GetServerInfo`.

TLS is 1.3-only (`tls.allowTls12` to relax; macOS cannot serve 1.3, so it allows 1.2 there too), and a
generated certificate is ECDSA P-256. A long-lived **server identity key** (`ServerIdentityKey`,
`server-identity.key`) signs the certificate's fingerprint; `GetServerInfo` serves the key and
signature, and clients pin the key so a replaced certificate is followed only when that key vouches
for it. See `docs/transport-security.md`. LiveKit's API URL must be https unless loopback or a
container name, and weak or `--dev` LiveKit secrets are refused (`LiveKitOptions.Validate`, which also
refuses a quoted, placeholder or barely-varied secret, and LiveKit's `ws://` client address pasted
into `apiUrl`).

Two consequences worth remembering:

- The identity key file **must persist** (and the certificate should). Losing the key locks out every
  client that pinned it. In Docker both live on a mounted volume for exactly this reason.
- Nothing sensitive goes in a URL or a header: ids, usernames and passwords travel inside protobuf
  request bodies; metadata carries only the session token.

This is transport encryption: the server can read everything it stores and relays. End-to-end
encryption sits on top — see **End-to-end encryption** below.

### End-to-end encryption

`RaindropServer/E2ee/`; full reference [`docs/e2ee.md`](docs/e2ee.md). MLS runs on clients (the Rust
core in `../RaindropCrypto`); **the server holds public keys and opaque blobs only**. Implemented so far:
the identity layer, the MLS delivery service, encrypted text messages and encrypted calls.

- **Identity key per user** (Ed25519, public half stored in `raindrop_user_identities`); the first
  device registered establishes it, later certificates signed by another key get `identity_mismatch`.
- **Devices** (`raindrop_devices`) are registered from a certificate (`E2ee/DeviceCertificate.cs`,
  parsed from the exact bytes the Rust core writes — the signature covers the ids *as text*, so they
  are never re-formatted) and revoked, never deleted.
- **Session binding**: a password opens a session, a device key *binds* it — `UserSession.DeviceId`,
  set by `DeviceService.BindSessionAsync` after the device signs a one-time challenge. Revoking another
  device, replacing the backup and approving a link need a bound session, so a password alone cannot
  take the owner's devices or backup away. Revoking a device ends its sessions.
- **Key backup** (`raindrop_key_backups`), **device linking** through `IDeviceLinkRelay` (in memory,
  opaque offer/approval; the link code's commitment stops the server substituting keys), **identity
  reset** with the password (publishes `e2ee.identity.reset`).
- **Delivery service** (`MlsDeliveryService`): one MLS group per channel (group id = channel UUID,
  16 big-endian bytes); the server reads only the cleartext envelope (`MlsFraming`). **One commit per
  epoch** — epoch bump and log insert in one transaction, and `raindrop_mls_commits`' key
  `(channel_id, epoch)` refuses a second; the loser gets `epoch_conflict`. Every call needs a bound
  session. Who belongs is `IE2eeMembershipPolicy` (default: `view_channel`, plus `connect` for voice),
  checked on every request, fan-out, claim and Welcome, so lost access cuts traffic at once;
  `GroupMembershipReconciler` sends `e2ee.group.reconcile` on role/member/device changes and members
  commit the difference against `groups/roster` — the server cannot change a group. Key packages
  (one-time + last resort), Welcomes addressed to recipient devices the committer names, a bounded
  commit log (`resyncRequired` beyond it). Key packages must be `MLSMessage`-wrapped.
- **Encrypted messages** (`TextChannelService.SendEncryptedAsync`, `SendEncryptedMessage`): an MLS
  application message for the channel's group, epoch within `MaximumPastEpochs` of the group's
  (`stale_epoch` otherwise), from a bound session; stored as ciphertext in
  `raindrop_encrypted_messages` (a new table — `RelationalTextMessageStore` routes by
  `TextMessageRecord.IsEncrypted` and merges both tables for history under the same cursor), never
  archived. Plaintext `SendMessage` answers `encryption_required` unless `channels.allowPlaintextMessages`;
  the console's `purge-plaintext` deletes old plaintext and the deleted-message archive.
- **Attachments** (`RaindropServer/Attachments/`, full reference [`docs/attachments.md`](docs/attachments.md)):
  files are encrypted by the sender under a per-file key (the Rust core's chunked `raindrop/attachment/v1`
  format), streamed up as ciphertext (`Upload`, a client stream of 256 KiB frames the service writes in
  8 MiB chunks), and named by id in `SendEncryptedMessage`'s `attachment_ids`; the key
  travels inside the MLS message. The server sees only a file's channel, declared extension and size — the
  ciphertext length fixes the plaintext length exactly (`AttachmentFormat`), which is how
  `attachments.maximumFileSizeMegabytes` is enforced without trusting the client.
- **Reports** (`MessageReportService`): a device attests every message it sends (Ed25519 over the
  channel, client message id, time, type and body, inside the encrypted payload); a reporter reveals
  the text and the attestation, the server verifies it against the sending device's key and stores
  it in `raindrop_message_reports` — the only encrypted-message text the server ever holds — and
  sends `message.reported` to channel moderators (`manage_messages`). Console: `reports`. A message with
  files is attested under `raindrop/message-attestation/v2`, which also signs each file's id and ciphertext
  hash; the server checks those against the files stored with the message.
- Ed25519 verification is BouncyCastle, confined to `Security/Ed25519Signatures.cs` (.NET has none).
- Seams: `IDeviceStore`/`IKeyBackupStore` (driver factories with refusing defaults), `IDeviceLinkRelay`,
  `IDeviceListener`, `ISessionStore.BindDevice`/`RemoveAllForDevice` (refusing defaults), `E2eeOptions`;
  `IE2eeMembershipPolicy`, `IMlsGroupStore`/`IKeyPackageStore`/`IWelcomeStore`; registered by
  `AddRaindropE2ee`, called from `AddRaindropServer`. Capabilities `e2ee.devices.v1`, `e2ee.delivery.v1`;
  `attachments.v1` while attachments are enabled.

### Users

`RaindropServer/Users/` holds the domain; `UserGrpcService` its wire surface.
`UserService` owns every rule, so the service stays thin and drivers stay dumb. The domain's contracts
live in the library next to `UserService` — it returns them, and the gRPC layer maps them.

The lifecycle (`UserStatus`) is the thing to keep straight:

- **First login is registration.** A client arrives with only its GUID, claims an unused username and
  sets a password, and gets a session back in the same call.
- **Usernames are unique per server**, compared on a lower-cased `username_normalized` column with a
  unique index. The same person can be `alice` here and `alice42` elsewhere.
- **Sessions live in memory only** (`InMemorySessionStore`) and are deliberately never persisted, so
  a restart logs everyone out. Clients log out when they close; a 12-hour idle timeout is the backstop
  (an open event stream renews its session, and its heartbeat timeout ends it when the client is gone).
- **Leaving preserves or erases.** `LeftDataPreserved` keeps the username (still reserved) and
  password until `data_expires_at`, capped at two years by `UserOptions.MaximumDataRetention`;
  `RetentionCleanupService` sweeps expired ones on a timer and at startup. `Erased` nulls the
  username and password immediately.
- **The UUID row always survives.** Messages are linked to the UUID, so an erased account leaves a
  tombstone row rather than a deleted one — and that UUID may register again from scratch.

Passwords are PBKDF2-HMAC-SHA256, 210,000 iterations, per-password salt, stored self-describing as
`pbkdf2-sha256$<iterations>$<salt>$<hash>` so the work factor can be raised later without
invalidating anything.

### Members

`RaindropServer/Members/` is the roster: who is on this server, and whether each of them is around
right now. There is no membership table — **a member is an active user** — because a Raindrop server
is one place rather than a directory of many, so `MemberService` reads the user store and asks an
`IPresenceTracker` for the half that is not persisted. `MemberService.ListMembers` is its whole wire surface.

**Presence is derived, never stored.** `PresenceStatus` is `Online` or `Offline`, and
`StreamPresenceTracker` — the default `IPresenceTracker` — answers it from the event stream registry: a
member is online exactly while they have an event stream open. The stream ends when its session does,
and when its heartbeats stop for `EventOptions.HeartbeatTimeout` (60 s), so a client that was killed
rather than closed reads as offline about a minute later. Everybody is offline after a restart until
their clients reconnect. `PresenceBroadcaster` (an `IEventStreamListener` and `ISessionListener`)
publishes `member.presence` when a member's first stream opens or last one closes.

The enum is deliberately open-ended, and the thing that makes adding to it cheap is that nothing
persists its numbers. It is **declared in the order a roster shows them** and sorted on that
declaration order, so `Idle` or `DoNotDisturb` is added by inserting a value between `Online` and
`Offline` — no renumbering, no migration, and no separate ranking table to keep in step. What the
wire carries is the protobuf enum `raindrop.v1.Presence` (next free number for a new status), so
clients sort by their own copy of that order and a client too old to know a status reads
`PRESENCE_UNSPECIFIED`, which it shows as offline.

`IEventConnectionRegistry.ConnectedUserIds()` answers for the whole roster at once, so a roster costs
one call however many members it has.

The roster is unpaged, and `IUserStore.ListByStatusAsync` is what it reads. A list sorted by presence
cannot be paged by the database anyway, since presence is not a column — a server large enough to
need paging needs presence to live somewhere queryable first.

One naming trap worth knowing: a member's `presence` is not their `state`. `state` (`active`,
`left_preserved`, `erased`) is the membership lifecycle from `Users/`; `presence` is whether they are
around — listening on an event stream. Members who have left do not appear in the roster at all.

### Roles and permissions

`RaindropServer/Roles/` decides what anybody is allowed to do; `RoleGrpcService` is its wire surface. **A role is a named set of permissions, a member holds any number of them, and what
they may do is the union.** Roles live on the server and are keyed to the member's UUID, matched at
login like everything else about them. A client is *told* what it may do, so it can grey out a
button rather than let a request fail — but it is never trusted about it: every check runs here.

`Permission` is a `[Flags]` enum backed by `long` rather than `int`: the set lives in one database
column, there is no migration mechanism here (see **Database modules**), so the room for another
fifty permissions has to exist from the start. The permissions are `ViewChannel`, `SendMessages`,
`AttachFiles` (add files to messages, checked in the channel beside `SendMessages`), `ManageMessages`, `CreateChannels`, `RenameChannels`, `DeleteChannels`, `ManageRoles`,
`Administrator`, and for voice `Connect`, `Speak`, `Video`, `ScreenShare` and `MuteMembers` (the last checked server-wide,
never per channel, and not part of the channel-creator rule — a mute for everyone follows its target
into every call).

Two conventions hold everywhere:

- **Permissions travel as names, never as a bitfield.** `view_channel`, `manage_roles`, and so on. A
  client too old to know a permission lists the ones it understands and ignores the rest, exactly as
  it does with `PresenceStatus` and `ChannelType`. `PermissionNames` is the single table everything
  is derived from — the wire names, the parser, and the catalogue `RoleService.ListPermissions`
  serves — so adding a permission means adding one row to it.
- **`Administrator` is expanded, never compared literally.** `PermissionExtensions.Expand` turns it
  into every permission this build has, so an administrator is never refused something merely
  because the bit for it was not set when their role was written. That is what keeps the owner role
  correct as the enum grows.

#### The two built-in roles

Seeded by `RoleService.EnsureBuiltInRolesAsync`, which `InitializeRaindropDatabaseAsync` calls
alongside the schema — before `ServerHost.Start`, deliberately not as a hosted service, because a
hosted service has no guaranteed ordering against Kestrel and the very first request to a fresh
server is somebody registering and expecting to own it.

- **`everyone`** — what an account can do here before anybody has granted it anything. Nobody is
  *assigned* it; holding it is what being an active member means, so the resolver folds it in for
  everyone and it is refused as a target for assign and unassign. Its permissions are editable (that
  is how an owner closes a server down to read-only); its name is not, and it cannot be deleted. It
  is seeded from `RoleOptions.DefaultMemberPermissions` — view, post, attach files, create channels,
  connect, speak — and *only* seeded: changing that setting later does not reach back and overwrite what
  administrators have since made it. A permission a later build introduces for everybody reaches an
  already-seeded `everyone` role **once**, through `PermissionRollouts` (recorded in
  `raindrop_permission_rollouts`, so an owner who removes it keeps it removed). The list is in code
  on purpose: the shipped `raindrop.json` names defaults explicitly, and an owner's older copy must
  not decide whether members ever get a new permission.
- **`owner`** — holds `Administrator`, and is neither editable nor deletable. A server whose
  administrative role could be stripped of its permissions is a server nobody can administer again.

**The first UUID to register is granted the owner role**, by `OwnerClaim`, an `IRegistrationListener`
that `UserService` calls after the account row is written. That interface exists so `Users/` never
learns what a role is; a listener that throws is reported and stepped over, because the account is
already written and refusing the session would lock the client out of a server it has just joined.
Ownership is claimed only while nobody holds it, so the second member to register is an ordinary one,
and it can be turned off with `RoleOptions.FirstMemberBecomesOwner` on a server whose owner is
granted the role from the console instead.

#### Who may change what

Every write goes through `RoleService`, and two rules do the work:

- **The built-in roles are protected**, as above.
- **Nobody may hand out what they do not hold.** Creating, editing, deleting, assigning or
  unassigning a role is refused unless the caller already holds every permission involved — and for
  an edit, that means the set the role *already* grants as well as the set it is being given, or
  editing would be escalation by another route. So `ManageRoles` lets somebody delegate their own
  authority and no more; it is not a path to `Administrator`. Holding `Administrator` satisfies the
  rule trivially, which is the point of it.

One more guard: **the last member holding the owner role keeps it.** There is no account above them
to put a server with no owner right again. Ownership is transferred by granting the role to somebody
else first.

#### Resolving

`RolePermissionResolver` is the only implementation of `IPermissionResolver`, and every permission
check in the server comes through its one method. It re-reads membership from the user store first —
a session outlives the account behind it, so a member who left in one session cannot keep acting
through a token still held in another. That check is also what makes it safe to keep role
assignments after somebody leaves: they grant nothing until the account is active again, and are
still there if they rejoin.

Roles are **server-wide**, so the `ChannelRecord?` parameter only changes the answer for the one rule
that is about a particular channel: **whoever creates a channel moderates it** — renaming, deleting,
and removing other members' messages in it — whatever their roles say. That is the rule the server
ran on before it had roles, and it keeps a member who can create a channel from being unable to tidy
it up afterwards; `RoleOptions.ChannelCreatorModeratesOwnChannels` turns it off for a server where
roles are meant to be the only answer. Per-channel role overrides, if they are ever wanted, go here
and nothing outside this class changes.

Drawing a channel list asks the resolver once per channel, so the union of a member's roles is cached
in `RolePermissionCache` and every write through `RoleService` clears the whole thing. The
invalidation is blunt on purpose: role changes are rare, a stale permission is a security bug, and a
cold cache costs one query. It is correct because a Raindrop server is a single process — the same
assumption `InMemorySessionStore` makes. A role granted directly in SQL takes effect on the next
restart, which is why the console commands go through the running server's own `RoleService`.

### Channels

Channels are how members talk to each other. There are two kinds, and the split runs all the way
down the code:

- **Text channels** — messages in chronological order. Implemented.
- **Voice channels** — a group call with camera video and screen sharing. Implemented, with the
  audio on a LiveKit sidecar — see **Voice** below.

`RaindropServer/Channels/` holds what every channel has in common — the record, the name policy,
permissions, and `ChannelService` for the lifecycle (create, list, get, rename, delete).
`RaindropServer/Channels/Text/` holds everything that is only true of text channels, and
`RaindropServer/Channels/Voice/` everything only true of voice channels — added without touching
`ChannelService`. On the wire the split is `ChannelService`, `TextChannelService` and `VoiceService`.

What keeps the shared half from growing a `switch` over the type is `IChannelModule`: one small
registration per kind of channel, saying which type it implements and how to delete what a channel
of that type holds. `ChannelService` refuses to create a type no module claims — which is the single
line of defence that keeps a channel type from appearing before the code that runs it — and calls
the module when a channel is deleted, so the shared code never learns what a message is. The module is
handed the whole `ChannelDeletion` (channel record, who, when) rather than an id, so it can preserve what it
is about to destroy. A module depends on stores, never on its type's service: every module is a
dependency of `ChannelService`, which the type's service depends on in turn, so the reverse would be a
cycle (`ChannelAudience.For` is the static a module uses for its events' audience).

Channel names are unique per server, on a lower-cased `name_normalized` column with a unique index,
so `#general` means one place to everybody. Same rule, same reasoning, and nearly the same policy
class as usernames.

#### Messages

A message belongs to a channel and is tied to the author's **UUID** — never to their account, which
may be erased, and never to their username, which may change. `UserService.LookupUser` is how a
client turns that UUID into a name to show.

**Deleting a message keeps the row.** The text is nulled and `deleted_at` / `deleted_by` are set, so
the message stays in place as a tombstone the client renders as "message deleted". The same shape as
an erased user: the tie between a message and a UUID is not something anyone can remove. It also
keeps history paging honest, because the row a client's cursor points at cannot vanish underneath it.
Deleting a *channel*, by contrast, does remove its messages — the module for the type does that just
before the channel row goes.

**But the text is copied first.** Nulling it would destroy it, so before it goes the original is written to
`raindrop_deleted_messages` (message id, channel id and name, author, content, sent/deleted times, who deleted
it, and an open-string `reason`: `message_deleted` or `channel_deleted`) — for a single message and for every
message in a deleted channel. It is **for the server owner, in the database, and nowhere else**: no endpoint,
permission or event exposes it and no client is sent it. It is not erasure — it outlives an erased author's
account, with no retention sweeper — so clients say "the server owner keeps a copy" when asking to confirm.
The copy is made by `ArchivingMessageDeletionListener`, the default `IMessageDeletionListener` (the plugin
seam: register more with `TryAddEnumerable`, they run beside it). Listeners run **before** the text is
destroyed and are **fail-closed**, unlike `IRegistrationListener`: one that throws stops the delete with
nothing changed and nothing published, so they must be idempotent (the archive's key is the message id and
its insert skips what is there). Full detail: [`docs/messages.md`](docs/messages.md).

Who may delete what — two independent rules:

- **Anyone may always delete their own message.** No permission is involved (not `ManageMessages`, not
  `SendMessages`); they only need to be able to see the channel.
- Deleting **someone else's** needs `ManageMessages` in that channel.

History is paged backwards, newest page first, through an opaque cursor. The cursor is a
`(sent_at, message_id)` pair rather than a timestamp alone, because two messages can share a
timestamp and the id breaks the tie — so a page boundary always falls between two specific rows and
paging can neither repeat nor skip a message. Each page arrives in chronological order, the way it is
rendered; `next_cursor` leads further into the past and is absent at the beginning of the channel.

#### Permissions

Channels own no permission rules of their own: every check goes through the one `IPermissionResolver`
that `Roles/` registers — see **Roles and permissions** below. What channels contribute is which
permission each operation needs (`SendMessages` to post, `RenameChannels` to rename,
`DeleteChannels` to delete, `ManageMessages` to remove somebody else's message) and the fact that
`ChannelService.ResolveAsync` asks once and hands the answer to the type's own service.

Every channel a client is shown carries `your_permissions`, so it can grey out what the caller cannot
do instead of guessing at the server's rules. A channel the caller may not see is reported as
`channel_not_found` rather than forbidden, so the API cannot be used to enumerate channels somebody
has been kept out of.

### Voice

`RaindropServer/Channels/Voice/`; full reference [`docs/voice.md`](docs/voice.md). **Raindrop is the
authority, the media server only carries media.** `VoiceService` decides who may join (`connect`),
speak (`speak`, and no mute for everyone), show their camera (`video`, within the server's per-call
cap), share their screen (`screen_share`, within its own cap) and moderate (`mute_members`); the
transport is told the outcome as one `VoicePublishGrant` — a join token whose `canPublishSources`
names exactly the granted sources, `UpdateParticipant` when that changes mid-call.

- **Seams**: `IVoiceTransport` (LiveKit via `AddRaindropLiveKit`, registered *before*
  `AddRaindropServer`; otherwise `UnavailableVoiceTransport` refuses every join with
  `voice_unavailable`), `IVoiceSignalRelay`, `IVoiceStateStore` (in-memory — a call cannot outlive the
  sessions in it), `IVoiceMuteStore` (persisted, `raindrop_voice_mutes`), `IVoiceJoinPolicy` (plugins
  may refuse a join). Leave reasons and transport names are open strings.
- **No webhooks.** LiveKit's would have to call Raindrop's self-signed HTTPS. Join and leave are gRPC
  calls (the roster updates instantly; a joiner is *pending* until LiveKit reports them), and
  `VoiceMaintenanceService` — one loop, so work and reconciliation never race — reconciles every 5 s
  both ways: drops participants LiveKit has not seen for 20 s and sessions that ended, and **removes
  anybody in a Raindrop room Raindrop did not admit**. That matters because self-hosted LiveKit does not
  revoke a removed participant's token; tokens therefore live 2 minutes (LiveKit refreshes them for
  connected clients). At startup it deletes leftover rooms (every session died with the old process);
  on shutdown it ends every call.
- **The signalling relay** — the one route that is not gRPC. Clients connect LiveKit's SDK to
  `/api/channels/voice/signal` on the server they already pin (mapped by `VoiceSignalRoutes` when a
  transport registers an `IVoiceSignalRelay`), and `LiveKitSignalRelay` forwards only `rtc` and
  `rtc/validate` — one certificate, LiveKit's HTTP port stays private, and the relay refuses a token
  whose member Raindrop has not admitted to that room. Only LiveKit's media ports (UDP 7882, TCP 7881)
  face the internet.
- **The room API is binary Twirp.** `LiveKitRoomClient` posts `application/protobuf` with the RoomService
  messages vendored in `RaindropServer.Voice.LiveKit/Protos/` (copied from `livekit/protocol` at the
  commit the pinned `livekit/livekit-server` image uses — move the two together); only Twirp's error
  bodies are JSON, by the Twirp spec.
- **Calls are end-to-end encrypted** (`e2ee.voice.v1`): clients' LiveKit frame cryptor encrypts every
  frame with a key exported from the voice channel's MLS group (`raindrop/voice/v1`, slot `epoch mod
  16`), so LiveKit forwards media it cannot hear or see; tokens grant no data channel, so no key
  material crosses LiveKit. One key per call covers voice, camera and screen alike — LiveKit's shared-key
  provider holds one key per ring slot for the whole room, so there is no second label for video, and
  neither the camera nor screen sharing needed a crypto change. The server holds no key; see
  `docs/voice.md#end-to-end-encrypted-calls`.
- **Camera video** (`voice.video.v1`, advertised when a transport is configured and
  `voice.maximumCameraPublishers` is above zero): **the camera grant follows the camera being on, not
  the permission.** A join token names `microphone` only; `SetVoiceSelfState(camera_on: true)` checks
  `video`, counts the call's cameras against the cap inside `IVoiceStateStore.SetCameraAsync` (one
  whole decision, so two members racing for the last place cannot both win), and only then is `camera`
  added to the grant. A client that ignores a refusal still cannot send. See
  `docs/voice.md#camera-video`.
- **Screen sharing** (`voice.screen.v1`, advertised when a transport is configured and
  `voice.maximumScreenShares` is above zero) works the same way through
  `IVoiceStateStore.SetScreenShareAsync`. A share is a `screen` or a `window` (an enum on the wire,
  `ScreenShareKinds` names here); only a window may carry its own sound (`screen_share_audio` is never
  granted for a screen — the server cannot see what is captured, so the rule lives in the grant), and
  **that sound follows the right to speak**: refused while muted for everyone or lacking `speak`, and
  cleared mid-share when either happens. `SetVoiceSelfState` is the **whole state** — a client that
  leaves the share out of a mute toggle ends it. See `docs/voice.md#screen-sharing`.
- **One call per member per server**, enforced atomically by the state store; joining from a second
  device takes the call over; a leave names its channel and must come from the joining session.
- Anything slow (a role change re-checking every participant, telling LiveKit about a mute) goes
  through `VoiceWorkQueue`, because event handlers run inside the publish and session listeners inside
  the request. Deleting a voice channel ends its call best-effort — a LiveKit outage must not block it.

### Events (push)

`RaindropServer/Events/` pushes every change to the clients allowed to see it, over the bidirectional
gRPC stream `EventService.Connect` — same `authorization` metadata as every call. Full reference:
[`docs/events.md`](docs/events.md). The server sends `ready` first (with the heartbeat interval), then a
`ServerEvent` per change, a typed `oneof`; the client sends `Heartbeat` every 20 s and `Resubscribe`
to get a fresh `ready` without reconnecting. Everything else a client does stays a unary call.

**Events are notifications, not a source of truth.** Each carries the full current state of what it
is about, so a client merges by id; nothing is replayed, and a client that reconnects waits for
`ready` and then re-reads. Audiences use the same `IPermissionResolver` as every call —
`ChannelService.AudienceFor(channel)` for anything inside a channel — so a hidden channel produces no
events at all.

This is built as replaceable seams because a **plugin SDK** will expose them, and new features should
follow the same shape: `IEventPublisher` (how anything announces a change; event types are open
strings inside the server, plugins prefix theirs), `IEventConnection` + `IEventConnectionRegistry` +
`IEventEncoder` (the transport seam — the gRPC stream in `RaindropServer.Grpc/Events/` is one
implementation; `ProtobufEventEncoder` maps each built-in name to its `oneof` case and carries a
plugin's protobuf payload as `PluginEvent`), `IServerEventHandler<T>` (in-process subscribers,
registered with `AddServerEventHandler<T, THandler>`), `IEventStreamListener` (told when a member's
first stream opens or last one closes — presence), `Users/ISessionListener` (told when a member's
sessions change), and `Channels/Text/IMessageDeletionListener` (told *before* message text is destroyed
— not an event, and fail-closed), the voice seams in **Voice** above, and for files `IAttachmentStorage`
(where ciphertext is kept; a local folder by default), `IAttachmentPolicy` (enumerable: every policy
must accept an upload), `IAttachmentListener` (enumerable, told when a file is uploaded, sent or
removed) and `Channels/Text/IMessageAttachments` (how text channels hand files to whatever stores them).
`EventHub` is the default publisher and registry; it sends `ready` first, closes a stream whose session
ends (`session_ended`), renews an open stream's session, bounds each connection's queue (`too_slow` when
full), and closes everything on shutdown (`server_stopping`). A stream with no heartbeat for
`HeartbeatTimeout` ends as `connection_lost`. Publishing never fails or blocks the call that caused it.

Services publish **after** the write, from the library, so the console's `grant`/`revoke` reach
clients exactly like a call's.

### Database modules

`IDatabaseDriver` is the whole contract: a name, a store per kind of data (`IUserStore`,
`IChannelStore`, `IRoleStore`, `ITextMessageStore`, `IDeletedMessageStore`, `IVoiceMuteStore`,
`IAttachmentStore`, and `IDeviceStore`/`IKeyBackupStore`, whose factories have refusing default implementations so older
drivers still load), idempotent schema DDL (`EnsureSchemaAsync`), and its dev-only counterpart
`ResetSchemaAsync` (drops every table, refuses by default so an older driver still loads) — see
**Configuration** below for `database.wipeOnStartup`. Drivers
are discovered by `DatabaseDriverRegistry` from loaded assemblies, from `RaindropServer.Data.*.dll`
next to the executable, and from any `*.dll` in a `drivers/` folder — which is how a third-party
driver gets added without touching this repo. A driver is only *used* when the owner names it in
configuration, so a SQLite server never opens the Postgres or SQL Server libraries.

In practice a driver derives from `RelationalDatabaseDriver` and supplies three things: a
`DbConnection`, the DDL for its dialect, and — where the provider disagrees — how a timestamp is
bound to a parameter. Everything else is the `Relational*Store` family (`RelationalStore` holds the
ADO.NET plumbing they share), whose SQL stays inside the common subset of the three databases: named
`@parameters`, no dialect functions. Note that UUIDs are stored as 36-character text, not native GUID
columns, because the three databases disagree on GUID typing and byte order.

Byte-array parameters go through `RelationalStore.AddBytes`, which types them as binary even when null:
SQL Server rejects an untyped null for a `varbinary` column, where SQLite and PostgreSQL shrug.

The one statement that could not stay in that common subset is the row limit message history needs:
SQLite and PostgreSQL take `LIMIT`, SQL Server wants `OFFSET … FETCH`. So it is asked of the driver
through `ApplyRowLimit`, which SQL Server overrides and the others inherit.

Adding a driver: derive from `RelationalDatabaseDriver`, override `CreateConnection` and
`SchemaStatements` (as `protected override` — the base declares them `protected internal`), and give
it a unique `Name`. The tables are `raindrop_users`, `raindrop_channels`, `raindrop_text_messages`,
`raindrop_roles`, `raindrop_member_roles`, `raindrop_deleted_messages` (the owner-only archive of deleted
message text), `raindrop_permission_rollouts` (which one-time permission grants this database has had),
`raindrop_voice_mutes` (members muted for everyone), and for end-to-end encryption
`raindrop_user_identities`, `raindrop_devices`, `raindrop_key_backups`, and the delivery service's
`raindrop_mls_groups`, `raindrop_mls_commits`, `raindrop_mls_key_packages`, `raindrop_mls_welcomes`,
`raindrop_encrypted_messages` and `raindrop_message_reports`, and `raindrop_attachments` (records of
encrypted files; the bytes live in `IAttachmentStorage`) — everything after the first five added as new
tables, so existing databases gain them without an `ALTER`.

`EnsureSchemaAsync` **only creates what is missing** — there is no migration mechanism, so adding a
column to a table that already exists in the wild needs a guarded `ALTER TABLE` per dialect, which
SQLite makes awkward. That is why `raindrop_roles.permissions` is a `bigint` holding a flags enum
rather than a narrower column or a row per permission: the room to grow had to be there on day one.
The role table's `sort_order` column is spelled that way because `position` is a keyword in
PostgreSQL.

There are no foreign keys anywhere in the schema, `raindrop_member_roles` included — a message keeps
pointing at the UUID of an erased author, and the code, not the database, is what keeps rows
consistent. So deleting a role deletes its assignments explicitly.

### Wiring

- `AddRaindropServer(configureUsers?, configureChannels?, configureRoles?, configureEvents?,
  configureVoice?, configureAttachments?)` registers the domain: events, the user subsystem, roles,
  attachments, channels and every kind of channel this server can host, voice, and E2EE. No transport.
- `AddRaindropGrpc()` (in `RaindropServer.Grpc`) serves it: `AddGrpc` with `RaindropInterceptor` and the
  message limits, the `ProtobufEventEncoder`, and the route contributor mapping every service. Call it
  after `AddRaindropServer`.
- `AddRaindropAttachments(configure?)` registers `AttachmentService` (also the `IMessageAttachments` text
  channels use), the local-folder storage, the configured policy, the deletion listener, the maintenance
  sweep and the storage startup check. It runs **before** `AddRaindropChannels`, which otherwise fills in
  `NoMessageAttachments` (refuses every file).
- `AddRaindropEvents(configure?)` registers the publisher, the connection registry and the placeholder
  `ServerIdentity`. Every subsystem that publishes calls it itself; register your own `IEventPublisher`
  or `IEventConnectionRegistry` first to replace them.
- `AddRaindropRoles(configure?)` registers roles, the `IPermissionResolver` that reads them, and the
  `OwnerClaim` listener. `AddRaindropChannels` calls it, because channels own no permission rules of
  their own. Registering a different `IPermissionResolver` before this runs wins — everything here
  is `TryAdd`.
- `AddRaindropChannels(configure?)` registers the channel subsystem on its own. Each channel type is
  an `IChannelModule` registration here; that list is what decides which types can exist. It also
  registers the default `IMessageDeletionListener` (the archive), with `TryAddEnumerable`.
- `AddRaindropVoice(configure?)` registers the voice module, `VoiceService`, the in-memory state
  store, the background worker and `VoiceSignalRoutes`. `AddRaindropLiveKit(configure)` (in
  `RaindropServer.Voice.LiveKit`) registers the LiveKit transport and relay, and must run **before**
  `AddRaindropServer`, since the voice subsystem only fills in a transport when none is registered.
- `AddRaindropDatabase(options)` enables exactly one driver by name and registers its stores.
  Nothing is persisted until it is called.
- `InitializeRaindropDatabaseAsync()` creates missing tables **and seeds the two built-in roles**;
  call it before `ServerHost.Start` so both are in place before the first call.
- `ServerHost.Start(services, options)` validates the options, builds the `WebApplication`
  (`ServerHost.Build`: Kestrel, every registered descriptor, every `IRouteContributor` mapped) and
  starts it on a background `Task.Run` so the calling thread stays free for the console loop. It is
  `IAsyncDisposable`, and exposes `Services` so the console can act through the same instances the
  services use rather than writing behind the running server's back. Tests call `Build` directly with
  `TestServer` swapped in.

### APIs

Every service, RPC and message is in `../RaindropProto/raindrop/v1/`; the per-domain docs carry the
tables and rules: [users](docs/users.md#user-api), [members](docs/members.md#member-api),
[roles](docs/roles-and-permissions.md#role-api), [channels](docs/channels.md#channel-api),
[messages](docs/messages.md#message-api), [voice](docs/voice.md#voice-api), [E2EE](docs/e2ee.md#api),
[attachments](docs/attachments.md#attachment-api), [events](docs/events.md). Every error reason is in
`../RaindropProto/docs/errors.md`.

Login deliberately answers `invalid_credentials` for an unknown UUID as well as a wrong password, so
the API cannot be used to enumerate who is on the server. `UpdateRole` leaves out what it is not
changing (`optional` name and position, a `PermissionSet` wrapper — absent keeps, empty clears);
`AssignRole`/`UnassignRole` answer `changed: false` rather than an error when nothing changed. A channel
the caller may not see is `channel_not_found`, never forbidden. Uploads need `send_messages` and
`attach_files` in the channel and a bound session; downloads refuse everything unreachable as
`attachment_not_found`.


## Configuration

`ServerCli/raindrop.json` is copied next to the executable and read at startup; its sections are
`urls`, `tls`, `database`, `users`, `channels` (message length and history page sizes), `roles`,
`voice` and `attachments`; environment variables override it, which is how the container is configured: `RAINDROP_URLS`,
`RAINDROP_DB_PROVIDER`, `RAINDROP_DB_CONNECTION_STRING`, `RAINDROP_DEV_WIPE_DATABASE`,
`RAINDROP_TLS_ENABLED` (only `true` is accepted),
`RAINDROP_TLS_CERTIFICATE_PATH`, `RAINDROP_TLS_CERTIFICATE_PASSWORD`, `RAINDROP_TLS_IDENTITY_KEY_PATH`,
`RAINDROP_VOICE_PROVIDER`, `RAINDROP_VOICE_STARTUP_CHECK`, `RAINDROP_LIVEKIT_API_URL`, `RAINDROP_LIVEKIT_API_KEY`,
`RAINDROP_LIVEKIT_API_SECRET`, `RAINDROP_LIVEKIT_ALLOW_WEAK_SECRET`, `RAINDROP_ATTACHMENTS_ENABLED`,
`RAINDROP_ATTACHMENTS_PATH`, `RAINDROP_ATTACHMENTS_MAX_MB`.

The `attachments` section is `enabled`, `storagePath` (relative to the executable; back it up with the
database), `maximumFileSizeMegabytes` and `maximumFileSizeMegabytesByExtension` (a map, e.g.
`{ "mp4": 500 }` — `ServerConfig`'s strict walker treats a `Dictionary<string, T>` setting's keys as the
owner's and checks only its values), `allowedExtensions` (null = any), `blockedExtensions` (always wins;
defaults to programs such as `exe`), `allowFilesWithoutExtension`, `maximumFilesPerMessage`,
`pendingUploadExpiryHours`, `maximumPendingUploadsPerMember`, and `preview` (`extensions` clients show
inline, `autoLoadMaximumMegabytes`). `AttachmentOptions.Validate` reports malformed extensions (`.png`,
`PNG`), an extension both allowed and blocked, a size override or preview for a type never accepted,
and limits that contradict each other. See `docs/configuration.md#attachments`.

`database.wipeOnStartup` (`RAINDROP_DEV_WIPE_DATABASE`) is **development only**: when set, every
Raindrop table is dropped (`IDatabaseDriver.ResetSchemaAsync`, implemented once generically in
`RelationalDatabaseDriver` for all three shipped drivers via `DROP TABLE IF EXISTS`) before
`EnsureSchemaAsync` recreates them — `InitializeRaindropDatabaseAsync` runs this before schema
creation and logs it loudly. No confirmation, no undo; `DatabaseOptions.Validate` reports it as a
warning whenever it's on. `docker-compose.yml` wires it into the `server` service, off by default —
see `docs/local-development-and-docker.md#wiping-the-database-on-every-start`.

The `voice` section is `provider` (`livekit`, or null for no voice — the default), `maximumParticipants`,
`maximumCameraPublishers` (how many cameras one call carries at once; 0 switches camera video off and
withdraws `voice.video.v1`), `maximumScreenShares` (how many shares one call carries at once, 2 by
default; 0 switches screen sharing off and withdraws `voice.screen.v1`),
`startupCheck` (`off`/`warn`/`require`) and `livekit` (`apiUrl`, a private address clients never reach;
`apiKey`; `apiSecret`; `allowWeakSecret`). `livekit` with a missing key or secret is a startup error.

**There is no shipped LiveKit secret, and no `--dev` mode behind it.** `docker-compose.yml` runs
`livekit/livekit-server` from `livekit.yaml` with `LIVEKIT_KEYS` built from `LIVEKIT_API_KEY` /
`LIVEKIT_API_SECRET` in `.env`, and Raindrop must be given the same pair. `apiSecret` is generated
(`openssl rand -base64 48`), and `LiveKitOptions` refuses anything that is not a secret: shorter than
32 characters, one of LiveKit's own dev values, a placeholder nobody substituted, or long without
being varied. `allowWeakSecret` (`RAINDROP_LIVEKIT_ALLOW_WEAK_SECRET`) downgrades those refusals to a
warning — it is the escape hatch for a throwaway LiveKit on your own machine, and nothing writing
about voice should present it as the ordinary way to run one.

**Everything in the file is validated before the server listens, and every problem is reported at
once** rather than one per restart. Each component validates its own settings by taking a
`ConfigurationReport` (`RaindropServer/Configuration/`) — `DatabaseOptions`, `UserOptions`,
`ChannelOptions`, `RoleOptions`, `VoiceOptions`, `LiveKitOptions`, `ServerHostOptions` — while
`ServerConfig.Load` checks the file itself: JSON that does not parse, a setting this build does not
have (an error, with the nearest real name suggested, because the deserializer would silently ignore
it and leave a default nobody chose), a value of the wrong kind, and `RAINDROP_*` variables that are
empty, malformed or misspelled. What can only be found out by asking is an `IStartupCheck`, run by
`services.RunRaindropStartupChecksAsync()` after registration and before the port opens; a plugin
registers its own with `AddStartupCheck<T>()`. `LiveKitStartupCheck` is the one this build ships:
LiveKit's host resolves, it answers, it is LiveKit, it accepts this server's key and secret, and its
clock is within 30 seconds of ours. `voice.startupCheck` decides whether an *unreachable* media
server is a warning or a refusal to start; credentials it actively refuses are an error either way,
since waiting cannot fix them. See `docs/configuration.md` and `docs/voice.md`.

Connection string examples per provider:

- `sqlite` — `Data Source=raindrop.db`
- `postgres` — `Host=localhost;Port=5432;Database=raindrop;Username=raindrop;Password=…`
- `sqlserver` — `Server=localhost,1433;Database=raindrop;User Id=raindrop;Password=…;TrustServerCertificate=True`

The `roles` section is `defaultMemberPermissions` (permission *names*, seeding the `everyone` role
the first time this server runs and never afterwards), `channelCreatorModeratesOwnChannels` and
`firstMemberBecomesOwner`. A permission name this build does not have is a startup error naming the
setting, rather than a role that silently grants less than the owner meant.

Relative TLS certificate paths resolve against the executable's directory; the SQLite path is
relative to the working directory. TLS cannot be switched off: `tls.enabled: false` and any `http://`
URL are startup errors (the setting is still read so an older file loads). A reverse proxy in front
of the server has to pass TLS through rather than terminate it.

## Docker Compose: the whole stack, or just its components

`docker-compose.yml`'s `server` service builds and runs the server itself (`ServerCli/Dockerfile`),
wired by default to `postgres` and `livekit` — `depends_on` starts both automatically:

```bash
cp .env.example .env   # then set POSTGRES_PASSWORD and LIVEKIT_API_SECRET
docker compose up -d server
```

`postgres`, `sqlserver` and `livekit` can also run on their own, for `dotnet run` development against
a real database instead of the default SQLite, or a LiveKit sidecar for voice calls (configured by
`livekit.yaml`, key and secret from `LIVEKIT_API_KEY`/`LIVEKIT_API_SECRET` in `.env`; the server
refuses `--dev` credentials; the image is pinned to the release whose RoomService messages
`RaindropServer.Voice.LiveKit/Protos/` holds). `docker compose up -d livekit`, then run with
`RAINDROP_VOICE_PROVIDER=livekit` and the *same* pair as `RAINDROP_LIVEKIT_API_KEY` /
`RAINDROP_LIVEKIT_API_SECRET` — LiveKit rejects everything signed with any other secret;
`LIVEKIT_NODE_IP` in `.env` is the address LiveKit advertises for audio (127.0.0.1 for clients on
this machine, the LAN address otherwise).

```bash
cp .env.example .env   # then set real passwords
docker compose up -d postgres      # or: sqlserver
```

Each service bind-mounts its data to `./data/<service>` on the host (gitignored), so `docker compose
down` — even `down -v` — and image updates never lose it; only deleting `./data` does. Point a
`dotnet run` server at whichever one is running:

```bash
RAINDROP_DB_PROVIDER=postgres RAINDROP_DB_CONNECTION_STRING="Host=localhost;Port=5432;Database=raindrop;Username=raindrop;Password=<POSTGRES_PASSWORD>" dotnet run --project ServerCli
```

```bash
RAINDROP_DB_PROVIDER=sqlserver RAINDROP_DB_CONNECTION_STRING="Server=localhost,1433;Database=raindrop;User Id=sa;Password=<MSSQL_SA_PASSWORD>;TrustServerCertificate=True" dotnet run --project ServerCli
```

The SQL Server image is amd64-only; on Apple Silicon it runs under emulation (`platform:
linux/amd64` in the compose file) — slow but correct. `mcr.microsoft.com/azure-sql-edge` is a
native-arm64 alternative that speaks the same TDS protocol, if you'd rather swap images than eat the
emulation cost.
