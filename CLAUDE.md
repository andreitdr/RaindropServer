# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Documentation wiki

[`docs/`](docs/README.md) is a per-feature documentation wiki for this project, meant to stay current.
**Whenever you change a feature or add a new one — a new endpoint, a changed contract, a new
permission, a new database driver, a new config option — update the matching file(s) under `docs/` as
part of that same change**, not as a follow-up. If nothing existing fits, add a new file and link it
from `docs/README.md`.

## Commands

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
- the MLS envelope parser and device certificates, against real fixtures from the Rust core
  (`RaindropServer.Tests/Fixtures/*.hex`)
- Ed25519 (RFC 8032 vectors) and the server identity key
- the stores under real concurrency: racing commits for one epoch, racing key-package claims, history
  paging across plaintext and encrypted messages
- the delivery-service and device-service rules
- LiveKit option validation

Store tests run on SQLite, and also on PostgreSQL and SQL Server when `RAINDROP_TEST_POSTGRES` or
`RAINDROP_TEST_SQLSERVER` holds a connection string (the docker-compose services will do). End-to-end
tests against a running server, with the real Rust core, are in the client:
`RaindropClient/test/integration/`.

Once the CLI is running, exercise endpoints with curl. The server presents a self-signed
certificate by default, so `-k` is needed until you trust it:

```bash
curl -k https://localhost:5001/api/server/info
```

```bash
curl -k -X POST https://localhost:5001/api/users/register -H "Content-Type: application/json" -d '{"userId":"<uuid>","username":"alice","password":"correct horse"}'
```

Authenticated calls carry the token that came back from registering or logging in:

```bash
curl -k https://localhost:5001/api/members -H "Authorization: Bearer <token>"
```

```bash
curl -k -X POST https://localhost:5001/api/channels/create -H "Content-Type: application/json" -H "Authorization: Bearer <token>" -d '{"type":"text","name":"general"}'
```

```bash
curl -k -X POST https://localhost:5001/api/channels/text/messages/send -H "Content-Type: application/json" -H "Authorization: Bearer <token>" -d '{"channelId":"<uuid>","content":"hello"}'
```

The first UUID to register owns the server, so its token is the one that can make roles:

```bash
curl -k -X POST https://localhost:5001/api/roles/create -H "Content-Type: application/json" -H "Authorization: Bearer <owner token>" -d '{"name":"Moderator","permissions":["manage_messages","rename_channels"]}'
```

```bash
curl -k -X POST https://localhost:5001/api/roles/assign -H "Content-Type: application/json" -H "Authorization: Bearer <owner token>" -d '{"userId":"<uuid>","roleId":"<uuid>"}'
```

In the running CLI's console, `drivers` lists the installed database modules, `db` shows the active
one, `fingerprint` prints the TLS fingerprint for clients to pin, `roles` lists the server's roles
and what each grants, `grant <user> <role>` and `revoke <user> <role>` change who holds one,
`events` shows how many clients have the event stream open, `voice` lists who is in which voice call,
`purge-plaintext` deletes every pre-encryption message and the deleted-message archive (after a `yes`),
`reports` shows the latest reported messages,
and `quit` shuts the server down gracefully. With no console attached (a container started
without a TTY) the server keeps serving until it receives SIGINT/SIGTERM.

Shutdown is bounded by `ServerHostOptions.ShutdownTimeout`, five seconds by default rather than the
host's usual thirty. A connection still draining must not outlast the ten seconds `docker stop`
allows before it resorts to SIGKILL — a server killed mid-stop is one that never ran its graceful
shutdown at all.

Docker build (`ServerCli/Dockerfile`, built from the repo root as context since it references `../.dockerignore`):

```bash
docker build -f ServerCli/Dockerfile -t raindrop-server .
```

## Architecture

A solution of seven projects targeting `net10.0` (the seventh, `RaindropServer.Tests`, is the xUnit
suite):

- **RaindropServer** (`RaindropServer/`) — the library: hosts an ASP.NET Core app on a background
  thread, auto-discovers endpoints, and owns the domain layer behind them (users, sessions, password
  hashing, the member roster and presence, roles and permissions, channels and their messages, push
  events, the database abstraction).
  No HTTP endpoints live here.
- **RaindropServer.Data.Sqlite / .Postgres / .SqlServer** — the database modules. Each is a separate
  assembly holding one `IDatabaseDriver` and the client library for that database.
- **RaindropServer.Voice.LiveKit** — the media module: carries voice calls on a LiveKit sidecar
  (tokens, the room API, the signalling relay) behind `IVoiceTransport`. No packages — the JWTs and
  the Twirp JSON calls are hand-written. Idle unless `voice.provider` names it.
- **ServerCli** (`ServerCli/`) — the executable everyone runs. It reads `raindrop.json`, enables one
  database module, starts the server, hosts the owner's console, and **owns every endpoint**, under
  `ServerCli/Endpoints/`.

Raindrop is decentralised: every server is independent, and a client holds a **separate GUID per
server**. Nothing in here may assume another server exists.

Every server starts out identical because every owner runs this same CLI — what makes one server
different from another is its address and its users, and implicitly its database and its plugins.
So endpoints belong in `ServerCli/Endpoints/`; only the logic they call belongs in the library.

### Endpoint model

Implement one of four interfaces anywhere in a loaded assembly and it is mapped automatically — no
registration code. The last three are wrapped into an `IEndpoint` by an adapter, so `ServerHost`
only ever knows about `IEndpoint`.

| Interface | Gives you | Use when |
|---|---|---|
| `IEndpoint` | raw `HttpContext` | full control, or a GET with no request body |
| `ITypedEndpoint<TReq,TRes>` | typed request and response | an anonymous POST |
| `IAuthenticatedEndpoint` | `UserSession` + `HttpContext` | an authenticated GET |
| `IAuthenticatedTypedEndpoint<TReq,TRes>` | `UserSession` + typed request and response | an authenticated POST |

The authenticated pair resolve `Authorization: Bearer <token>` through `ISessionStore` and answer
401 before the handler runs, so handlers can assume a valid caller. The typed pair are *format*
agnostic — see Wire formats below — so a handler never knows whether it was called in JSON or binary.

Handlers signal failure by throwing `ApiException` (`ApiException.Conflict("username_taken", …)`);
every adapter turns it into the right status code with an `{ "error", "message" }` body, encoded in
whichever format the caller asked for. Raw `IEndpoint` implementations get the same treatment by
going through `EndpointResponse.WriteAsync`.
Adapters themselves carry `[SkipEndpointDiscovery]` so discovery does not pick them up.

### Wire formats

Bodies are encoded by an `IPayloadCodec`, negotiated per request from `Content-Type` and `Accept`:

- `application/json` — the default, and what you get from curl when you say nothing.
- `application/x-msgpack` — MessagePack, what a real client should use.

The two are interchangeable in both directions: a request can arrive as JSON and be answered in
MessagePack, and error bodies follow the same negotiation. `GET /api/server/info` advertises which
formats a server supports.

MessagePack is serialized contractless, so contracts stay plain records with no attributes. The
trade-off is that binary keys are the property names verbatim (`UserId`) where JSON camelCases them
(`userId`); a .NET client sharing the contract types never notices, a client in another language
needs to know. Typeless resolvers are deliberately not used, and deserialization runs under
`MessagePackSecurity.UntrustedData`.

Worth being honest about the payoff: on the small objects user management deals with, MessagePack
saves only ~15% of the bytes — GUIDs dominate, and the longer PascalCase keys eat into the gain. The
wins that matter are skipping text parsing entirely, HTTP/2 multiplexing every call over one
connection, and headroom for message history and voice metadata, where payloads are numeric and
repetitive and binary pulls far ahead.

### Transport security

Everything is TLS. `ServerHost` configures Kestrel with the certificate and enables HTTP/1.1 and
HTTP/2, which is negotiated by ALPN.

Raindrop servers are self-hosted on whatever address their owner has, and most will never have a
CA-issued certificate, so `ServerCertificate` generates a self-signed one on first start and saves
it. That encrypts the connection but proves nothing about who is on the other end, so **clients are
expected to pin the fingerprint**: a client already stores a per-server GUID, and stores the
server's SHA-256 fingerprint alongside it the first time it connects, refusing to continue if it
ever changes. Same trust-on-first-use model as SSH. Point `certificatePath` at a CA-issued PFX
instead and clients can verify it normally.

The fingerprint is printed at startup, available from the `fingerprint` console command, and served
by `GET /api/server/info`.

TLS is 1.3-only (`tls.allowTls12` to relax; macOS cannot serve 1.3, so it allows 1.2 there too), and a
generated certificate is ECDSA P-256. A long-lived **server identity key** (`ServerIdentityKey`,
`server-identity.key`) signs the certificate's fingerprint; `/api/server/info` serves the key and
signature, and clients pin the key so a replaced certificate is followed only when that key vouches
for it. See `docs/transport-security.md`. LiveKit's API URL must be https unless loopback or a
container name, and weak or `--dev` LiveKit secrets are refused (`LiveKitOptions.Validate`).

Two consequences worth remembering:

- The identity key file **must persist** (and the certificate should). Losing the key locks out every
  client that pinned it. In Docker both live on a mounted volume for exactly this reason.
- Nothing sensitive goes in a URL. The lookups that would naturally be GETs with query parameters
  (`status`, `username-available`, `lookup`) are POSTs carrying a body instead, so UUIDs and
  usernames stay inside the encrypted body rather than in a line that tends to end up in logs.

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
- **Encrypted messages** (`TextChannelService.SendEncryptedAsync`, `send-encrypted`): an MLS
  application message for the channel's group, epoch within `MaximumPastEpochs` of the group's
  (`stale_epoch` otherwise), from a bound session; stored as ciphertext in
  `raindrop_encrypted_messages` (a new table — `RelationalTextMessageStore` routes by
  `TextMessageRecord.IsEncrypted` and merges both tables for history under the same cursor), never
  archived. Plaintext `send` answers `encryption_required` unless `channels.allowPlaintextMessages`;
  the console's `purge-plaintext` deletes old plaintext and the deleted-message archive.
- **Reports** (`MessageReportService`): a device attests every message it sends (Ed25519 over the
  channel, client message id, time, type and body, inside the encrypted payload); a reporter reveals
  the text and the attestation, the server verifies it against the sending device's key and stores
  it in `raindrop_message_reports` — the only encrypted-message text the server ever holds — and
  sends `message.reported` to channel moderators (`manage_messages`). Console: `reports`.
- Ed25519 verification is BouncyCastle, confined to `Security/Ed25519Signatures.cs` (.NET has none).
- Seams: `IDeviceStore`/`IKeyBackupStore` (driver factories with refusing defaults), `IDeviceLinkRelay`,
  `IDeviceListener`, `ISessionStore.BindDevice`/`RemoveAllForDevice` (refusing defaults), `E2eeOptions`;
  `IE2eeMembershipPolicy`, `IMlsGroupStore`/`IKeyPackageStore`/`IWelcomeStore`; registered by
  `AddRaindropE2ee`, called from `AddRaindropServer`. Capabilities `e2ee.devices.v1`, `e2ee.delivery.v1`.

### Users

`RaindropServer/Users/` holds the domain; `ServerCli/Endpoints/Users/` holds its HTTP surface.
`UserService` owns every rule, so endpoints stay thin and drivers stay dumb. The request and response
contracts live in the library next to `UserService` — it returns them, and the library cannot depend
on `ServerCli`.

The lifecycle (`UserStatus`) is the thing to keep straight:

- **First login is registration.** A client arrives with only its GUID, claims an unused username and
  sets a password, and gets a session back in the same call.
- **Usernames are unique per server**, compared on a lower-cased `username_normalized` column with a
  unique index. The same person can be `alice` here and `alice42` elsewhere.
- **Sessions live in memory only** (`InMemorySessionStore`) and are deliberately never persisted, so
  a restart logs everyone out. Clients log out when they close; a 12-hour idle timeout is the backstop.
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
`IPresenceTracker` for the half that is not persisted. `GET /api/members` is its whole HTTP surface.

**Presence is derived, never stored.** `PresenceStatus` is `Online` or `Offline`, and
`SessionPresenceTracker` — the default `IPresenceTracker` — answers it from `ISessionStore`: a member
is online exactly when they hold a session that has not lapsed. That is as much as the server can
honestly know. Two consequences follow from sessions living in memory: everybody is offline after a
restart, and a client that was killed rather than closed reads as online until its idle timeout
expires. Shortening `UserOptions.SessionIdleTimeout` sharpens presence; a client heartbeat would
sharpen it further, and would go behind the same interface rather than into the session store.

The enum is deliberately open-ended, and the thing that makes adding to it cheap is that nothing
persists its numbers. It is **declared in the order a roster shows them** and sorted on that
declaration order, so `Idle` or `DoNotDisturb` is added by inserting a value between `Online` and
`Offline` — no renumbering, no migration, and no separate ranking table to keep in step. What the
wire carries is the *name* (`online`, `offline`), so clients sort by their own copy of that order and
a client too old to know a status simply does not render it.

`ISessionStore` gained one method for this, `ActiveUserIds()` — the whole set at once, so a roster
costs one call however many members it has. It reports *sessions*, not presence: what a session
implies is `Members/`'s business, which is what keeps the presence enum out of the user subsystem.

The roster is unpaged, and `IUserStore.ListByStatusAsync` is what it reads. A list sorted by presence
cannot be paged by the database anyway, since presence is not a column — a server large enough to
need paging needs presence to live somewhere queryable first.

One naming trap worth knowing: a member's `presence` is not their `state`. `state` (`active`,
`left_preserved`, `erased`) is the membership lifecycle from `Users/`; `presence` is whether they are
logged in. Members who have left do not appear in the roster at all.

### Roles and permissions

`RaindropServer/Roles/` decides what anybody is allowed to do; `ServerCli/Endpoints/Roles/` is its
HTTP surface. **A role is a named set of permissions, a member holds any number of them, and what
they may do is the union.** Roles live on the server and are keyed to the member's UUID, matched at
login like everything else about them. A client is *told* what it may do, so it can grey out a
button rather than let a request fail — but it is never trusted about it: every check runs here.

`Permission` is a `[Flags]` enum backed by `long` rather than `int`: the set lives in one database
column, there is no migration mechanism here (see **Database modules**), so the room for another
fifty permissions has to exist from the start. The permissions are `ViewChannel`, `SendMessages`,
`ManageMessages`, `CreateChannels`, `RenameChannels`, `DeleteChannels`, `ManageRoles`,
`Administrator`, and for voice `Connect`, `Speak` and `MuteMembers` (the last checked server-wide,
never per channel, and not part of the channel-creator rule — a mute for everyone follows its target
into every call).

Two conventions hold everywhere:

- **Permissions travel as names, never as a bitfield.** `view_channel`, `manage_roles`, and so on. A
  client too old to know a permission lists the ones it understands and ignores the rest, exactly as
  it does with `PresenceStatus` and `ChannelType`. `PermissionNames` is the single table everything
  is derived from — the wire names, the parser, and the catalogue `GET /api/roles/permissions`
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
  is seeded from `RoleOptions.DefaultMemberPermissions` — view, post, create channels, connect,
  speak — and *only* seeded: changing that setting later does not reach back and overwrite what
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
- **Voice channels** — a group call, later with camera and screen sharing. Implemented, with the
  audio on a LiveKit sidecar — see **Voice** below.

`RaindropServer/Channels/` holds what every channel has in common — the record, the name policy,
permissions, and `ChannelService` for the lifecycle (create, list, get, rename, delete).
`RaindropServer/Channels/Text/` holds everything that is only true of text channels, and
`RaindropServer/Channels/Voice/` everything only true of voice channels — added without touching
`ChannelService`. `ServerCli/Endpoints/Channels/` mirrors the same split.

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
may be erased, and never to their username, which may change. `POST /api/users/lookup` is how a
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
rendered; `nextCursor` leads further into the past and comes back null at the beginning of the
channel.

#### Permissions

Channels own no permission rules of their own: every check goes through the one `IPermissionResolver`
that `Roles/` registers — see **Roles and permissions** below. What channels contribute is which
permission each operation needs (`SendMessages` to post, `RenameChannels` to rename,
`DeleteChannels` to delete, `ManageMessages` to remove somebody else's message) and the fact that
`ChannelService.ResolveAsync` asks once and hands the answer to the type's own service.

Every channel a client is shown carries `yourPermissions`, so it can grey out what the caller cannot
do instead of guessing at the server's rules. A channel the caller may not see is reported as
`channel_not_found` rather than forbidden, so the API cannot be used to enumerate channels somebody
has been kept out of.

### Voice

`RaindropServer/Channels/Voice/`; full reference [`docs/voice.md`](docs/voice.md). **Raindrop is the
authority, the media server only carries audio.** `VoiceService` decides who may join (`connect`),
speak (`speak`, and no mute for everyone) and moderate (`mute_members`); the transport is told the
outcome as a grant — a join token whose `canPublish` says whether the microphone is accepted,
`UpdateParticipant` when that changes mid-call.

- **Seams**: `IVoiceTransport` (LiveKit via `AddRaindropLiveKit`, registered *before*
  `AddRaindropServer`; otherwise `UnavailableVoiceTransport` refuses every join with
  `voice_unavailable`), `IVoiceSignalRelay`, `IVoiceStateStore` (in-memory — a call cannot outlive the
  sessions in it), `IVoiceMuteStore` (persisted, `raindrop_voice_mutes`), `IVoiceJoinPolicy` (plugins
  may refuse a join). Leave reasons and transport names are open strings.
- **No webhooks.** LiveKit's would have to call Raindrop's self-signed HTTPS. Join and leave are REST
  (the roster updates instantly; a joiner is *pending* until LiveKit reports them), and
  `VoiceMaintenanceService` — one loop, so work and reconciliation never race — reconciles every 5 s
  both ways: drops participants LiveKit has not seen for 20 s and sessions that ended, and **removes
  anybody in a Raindrop room Raindrop did not admit**. That matters because self-hosted LiveKit does not
  revoke a removed participant's token; tokens therefore live 2 minutes (LiveKit refreshes them for
  connected clients). At startup it deletes leftover rooms (every session died with the old process);
  on shutdown it ends every call.
- **The signalling relay.** Clients connect LiveKit's SDK to `/api/channels/voice/signal` on the server
  they already pin, and `LiveKitSignalRelay` forwards only `rtc` and `rtc/validate` — one certificate,
  LiveKit's HTTP port stays private, and the relay refuses a token whose member Raindrop has not
  admitted to that room. Only LiveKit's media ports (UDP 7882, TCP 7881) face the internet.
- **Calls are end-to-end encrypted** (`e2ee.voice.v1`): clients' LiveKit frame cryptor encrypts every
  frame with a key exported from the voice channel's MLS group (`raindrop/voice/v1`, slot `epoch mod
  16`), so LiveKit forwards audio it cannot hear; tokens grant no data channel, so no key material
  crosses LiveKit. The server holds no key; see `docs/voice.md#end-to-end-encrypted-calls`.
- **One call per member per server**, enforced atomically by the state store; joining from a second
  device takes the call over; a leave names its channel and must come from the joining session.
- Anything slow (a role change re-checking every participant, telling LiveKit about a mute) goes
  through `VoiceWorkQueue`, because event handlers run inside the publish and session listeners inside
  the request. Deleting a voice channel ends its call best-effort — a LiveKit outage must not block it.

### Events (push)

`RaindropServer/Events/` pushes every change to the clients allowed to see it, over a WebSocket at
`GET /api/events` — same `Authorization: Bearer` header, same `Accept` negotiation (JSON in text
frames, MessagePack in binary). Full reference: [`docs/events.md`](docs/events.md).

**Events are notifications, not a source of truth.** Each carries the full current state of what it
is about, so a client merges by id; nothing is replayed, and a client that reconnects waits for
`ready` and then re-reads. Audiences use the same `IPermissionResolver` as every REST check —
`ChannelService.AudienceFor(channel)` for anything inside a channel — so a hidden channel produces no
events at all.

This is built as replaceable seams because a **plugin SDK** will expose them, and new features should
follow the same shape: `IEventPublisher` (how anything announces a change; event types are open
strings, plugins prefix theirs), `IEventConnection` + `IEventConnectionRegistry` (the transport seam —
WebSocket lives in `Events/WebSockets/` and is one implementation), `IServerEventHandler<T>`
(in-process subscribers, registered with `AddServerEventHandler<T, THandler>`),
`Users/ISessionListener` (told when a member's sessions change), and
`Channels/Text/IMessageDeletionListener` (told *before* message text is destroyed — not an event, and
fail-closed), and the voice seams in **Voice** above. `EventHub` is the default publisher
and registry; it sends `ready` first, closes a stream whose session ends (`4401`), renews an open
stream's session, bounds each connection's queue (`4429` when full), and closes everything with
`1001` on shutdown. Publishing never fails or blocks the request that caused it.

Services publish **after** the write, from the library, so the console's `grant`/`revoke` reach
clients exactly like the API's. Over HTTP/2 a WebSocket is an extended `CONNECT`, which is why
`ServerHost` maps every `GET` route for `CONNECT` as well.

### Database modules

`IDatabaseDriver` is the whole contract: a name, a store per kind of data (`IUserStore`,
`IChannelStore`, `IRoleStore`, `ITextMessageStore`, `IDeletedMessageStore`, `IVoiceMuteStore`, and
`IDeviceStore`/`IKeyBackupStore`, whose factories have refusing default implementations so older
drivers still load), and idempotent schema DDL. Drivers
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
`raindrop_encrypted_messages` and `raindrop_message_reports` — everything after the first five added as
new tables, so existing databases gain them without an `ALTER`.

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
  configureVoice?)` registers routing, events, the user subsystem, roles, channels and every kind of
  channel this server can host, voice, and every discovered endpoint.
- `AddRaindropEvents(configure?)` registers the publisher, the connection registry and the WebSocket
  transport. Every subsystem that publishes calls it itself; register your own `IEventPublisher` or
  `IEventConnectionRegistry` first to replace them.
- `AddRaindropRoles(configure?)` registers roles, the `IPermissionResolver` that reads them, and the
  `OwnerClaim` listener. `AddRaindropChannels` calls it, because channels own no permission rules of
  their own. Registering a different `IPermissionResolver` before this runs wins — everything here
  is `TryAdd`.
- `AddRaindropChannels(configure?)` registers the channel subsystem on its own. Each channel type is
  an `IChannelModule` registration here; that list is what decides which types can exist. It also
  registers the default `IMessageDeletionListener` (the archive), with `TryAddEnumerable`.
- `AddRaindropVoice(configure?)` registers the voice module, `VoiceService`, the in-memory state
  store, and the background worker. `AddRaindropLiveKit(configure)` (in `RaindropServer.Voice.LiveKit`)
  registers the LiveKit transport and relay, and must run **before** `AddRaindropServer`, since the
  voice subsystem only fills in a transport when none is registered.
- `AddRaindropDatabase(options)` enables exactly one driver by name and registers its stores.
  Nothing is persisted until it is called.
- `InitializeRaindropDatabaseAsync()` creates missing tables **and seeds the two built-in roles**;
  call it before `ServerHost.Start` so both are in place before the first request.
- `ServerHost.Start(services, urls)` copies the descriptors into a `WebApplication`, maps every
  `IEndpoint`, and starts on a background `Task.Run` so the calling thread stays free for the console
  loop. It is `IAsyncDisposable`, and exposes `Services` so the console can act through the same
  instances the endpoints use rather than writing behind the running server's back.

### User API

All routes are under `/api/users`. Authenticated ones take `Authorization: Bearer <token>`.

| Method | Route | Auth | Purpose |
|---|---|---|---|
| GET | `/api/server/info` | – | handshake: protocol version, TLS fingerprint to pin, supported formats |
| POST | `/api/users/status` | – | what this UUID means here: `unregistered`, `active`, `left_preserved`, `erased` |
| POST | `/api/users/username-available` | – | uniqueness pre-check for the registration screen |
| POST | `/api/users/register` | – | first login: claim a username and password, get a session |
| POST | `/api/users/login` | – | UUID (or username) + password → session |
| POST | `/api/users/rejoin` | – | restore a preserved account |
| POST | `/api/users/logout` | ✓ | end this session |
| GET | `/api/users/me` | ✓ | own profile |
| POST | `/api/users/lookup` | ✓ | resolve a UUID to a display name, e.g. for a message author |
| POST | `/api/users/me/password` | ✓ | change password; revokes all sessions and returns a new token |
| POST | `/api/users/me/username` | ✓ | claim a different username |
| POST | `/api/users/me/leave` | ✓ | leave the server, preserving or erasing data |

Error bodies are `{ "error": "<code>", "message": "<text>" }`. Codes clients branch on:
`username_taken`, `already_registered`, `account_preserved`, `account_left`, `invalid_credentials`,
`invalid_session`, `no_preserved_account`, `weak_password`, `invalid_username`, `invalid_payload`,
`unsupported_media_type`.

Login deliberately answers `invalid_credentials` for an unknown UUID as well as a wrong password, so
the API cannot be used to enumerate who is on the server.

### Event API

| Method | Route | Auth | Purpose |
|---|---|---|---|
| GET | `/api/events` | ✓ | WebSocket (HTTP/1.1 upgrade or HTTP/2 extended CONNECT) streaming every change the caller may see |

Event types: `ready`, `message.created`, `message.deleted`, `channel.created`, `channel.renamed`,
`channel.deleted`, `member.joined`, `member.renamed`, `member.left`, `member.presence`, `member.roles`,
`role.created`, `role.updated`, `role.deleted`, `voice.state`, `voice.left`, `e2ee.device.added`,
`e2ee.device.revoked`, `e2ee.identity.reset`, `e2ee.handshake`, `e2ee.welcome`, `e2ee.group.reconcile`,
`e2ee.keypackages.low`, `message.reported`. A non-WebSocket request gets `426 websocket_required`.
Close codes: `1001` server stopping, `1008` client sent data, `4401` session ended, `4429` too slow.

### E2EE API

All authenticated; see [`docs/e2ee.md`](docs/e2ee.md#api) for request shapes and error codes.

| Method | Route | Purpose |
|---|---|---|
| POST | `/api/e2ee/devices/register` | register a device from its certificate (first one sets the identity key) |
| POST | `/api/e2ee/devices/list` | identity keys and active devices of up to 500 users |
| POST | `/api/e2ee/devices/revoke` | revoke one of your devices (bound session, unless it is this one) |
| POST | `/api/e2ee/identity/reset` | with the password: forget identity key and backup, revoke every device |
| POST | `/api/e2ee/session/challenge` | one-time nonce to sign with a device key |
| POST | `/api/e2ee/session/bind` | bind this session to a device with that signature |
| GET | `/api/e2ee/backup` | the sealed key backup |
| POST | `/api/e2ee/backup/put` | replace it (bound session) |
| POST | `/api/e2ee/device-link/start` · `offer` · `approve` · `approval` | link a new device through the relay |
| POST | `/api/e2ee/groups/create` · `state` · `commit` · `info` · `sync` · `roster` | a channel's MLS group: start, read, commit (one per epoch), publish group info, catch up, who belongs |
| POST | `/api/e2ee/keypackages/upload` · `claim`, GET `…/count` | devices' key packages |
| GET | `/api/e2ee/welcomes`, POST `…/ack` | Welcomes waiting for this device |

### Member API

| Method | Route | Auth | Purpose |
|---|---|---|---|
| GET | `/api/members` | ✓ | every active member, with `presence` (`online`/`offline`), ordered by presence then username |

No error codes of its own beyond `invalid_session`, which is what a caller who is no longer a member
gets.

### Role API

All authenticated. Reading is open to any member — a role list is not a secret, and a client needs it
to show who moderates what. Writing needs `manage_roles` **and** every permission involved.

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/roles` | every role on this server, highest position first |
| GET | `/api/roles/me` | the caller's own roles, and what they may do server-wide |
| GET | `/api/roles/permissions` | the permissions this build understands, for a role editor |
| POST | `/api/roles/get` | one role, and the UUIDs holding it |
| POST | `/api/roles/create` | create a role |
| POST | `/api/roles/update` | rename it, replace its permissions, or move it |
| POST | `/api/roles/delete` | delete it and take it away from everyone holding it |
| POST | `/api/roles/assign` | give a member a role |
| POST | `/api/roles/unassign` | take a role away from a member |
| POST | `/api/roles/member` | which roles one member holds, and what they add up to |

`update` leaves out what it is not changing: `name`, `permissions` and `position` are each optional
and mean "leave this alone" when absent, so renaming a role cannot accidentally clear what it grants.
An **empty** permission list does clear them — that is the difference between absent and empty.

`assign` and `unassign` answer with `changed`, which is false when the member already held the role
or already did not. Neither is an error: the request describes a state, and that state now holds.

Role error codes on top of the user ones: `invalid_role_name`, `role_name_taken`, `role_not_found`,
`invalid_permission`, `role_immutable`, `role_not_assignable`, `last_owner`, `member_not_found`,
`missing_permission`.

### Channel API

All authenticated. Ids travel in the body rather than the path, for the same reason usernames do —
see Transport security.

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/channels` | every channel the caller may see, with `yourPermissions` for each |
| POST | `/api/channels/create` | create a channel of a given type: `text` or `voice` |
| POST | `/api/channels/get` | one channel, and what the caller may do in it |
| POST | `/api/channels/rename` | rename it — needs `RenameChannels`, or having created it |
| POST | `/api/channels/delete` | delete it and everything in it — needs `DeleteChannels`, or having created it |
| POST | `/api/channels/text/messages/send-encrypted` | post an end-to-end encrypted message — needs `SendMessages` and a bound session |
| POST | `/api/channels/text/messages/send` | post a plaintext message — `encryption_required` unless `channels.allowPlaintextMessages` |
| POST | `/api/channels/text/messages/report` | report an encrypted message, revealing its text with the author's attestation |
| POST | `/api/channels/text/messages/reports` | a channel's reports — needs `ManageMessages` |
| POST | `/api/channels/text/messages/history` | one page of history, newest page first; follow `nextCursor` |
| POST | `/api/channels/text/messages/delete` | delete a message: your own always, anyone else's with `ManageMessages` |
| GET | `/api/channels/voice` | whether voice works here, and who is in every call the caller can see |
| POST | `/api/channels/voice/join` | join a call — needs `Connect`; returns the transport, where to connect, and a short-lived token |
| POST | `/api/channels/voice/leave` | leave the named call (idempotent; only the joining session) |
| POST | `/api/channels/voice/state` | self mute / deafen, for everybody's icons |
| POST | `/api/channels/voice/mute` | mute or unmute a member for everyone — needs `MuteMembers` server-wide |
| GET | `/api/channels/voice/signal/{path}` | LiveKit's signalling, relayed (authenticated by LiveKit's token, not a session) |

Channel error codes on top of the user ones: `invalid_channel_type`, `channel_type_unsupported`,
`invalid_channel_name`, `channel_name_taken`, `channel_not_found`, `wrong_channel_type`,
`missing_permission`, `invalid_message`, `message_too_long`, `message_not_found`, `invalid_cursor`,
`encryption_required`, `device_required`, `invalid_mls_message`, `wrong_group`, `group_not_found`, `stale_epoch`,
and for voice `voice_unavailable`, `channel_full`, `not_in_voice`, `member_not_found`.


## Configuration

`ServerCli/raindrop.json` is copied next to the executable and read at startup; its sections are
`urls`, `tls`, `database`, `users`, `channels` (message length and history page sizes), `roles` and
`voice`; environment variables override it, which is how the container is configured: `RAINDROP_URLS`,
`RAINDROP_DB_PROVIDER`, `RAINDROP_DB_CONNECTION_STRING`, `RAINDROP_TLS_ENABLED`,
`RAINDROP_TLS_CERTIFICATE_PATH`, `RAINDROP_TLS_CERTIFICATE_PASSWORD`, `RAINDROP_VOICE_PROVIDER`,
`RAINDROP_LIVEKIT_API_URL`, `RAINDROP_LIVEKIT_API_KEY`, `RAINDROP_LIVEKIT_API_SECRET`.

The `voice` section is `provider` (`livekit`, or null for no voice — the default), `maximumParticipants`
and `livekit` (`apiUrl`, a private address clients never reach; `apiKey`; `apiSecret`). `livekit` with
a missing key or secret is a startup error.

Connection string examples per provider:

- `sqlite` — `Data Source=raindrop.db`
- `postgres` — `Host=localhost;Port=5432;Database=raindrop;Username=raindrop;Password=…`
- `sqlserver` — `Server=localhost,1433;Database=raindrop;User Id=raindrop;Password=…;TrustServerCertificate=True`

The `roles` section is `defaultMemberPermissions` (permission *names*, seeding the `everyone` role
the first time this server runs and never afterwards), `channelCreatorModeratesOwnChannels` and
`firstMemberBecomesOwner`. A permission name this build does not have is a startup error naming the
setting, rather than a role that silently grants less than the owner meant.

Relative TLS certificate paths resolve against the executable's directory; the SQLite path is
relative to the working directory. Set `tls.enabled` to false only when a reverse proxy terminates
TLS, and listen on `http://` in that case — an `https://` URL without a certificate is a startup
error rather than a silent downgrade.

## Local databases and LiveKit (Docker)

`docker-compose.yml` runs Postgres and SQL Server locally for development against a real database
instead of the default SQLite, and LiveKit (configured by `livekit.yaml`, key and secret from
`LIVEKIT_API_KEY`/`LIVEKIT_API_SECRET` in `.env`; the server refuses `--dev` credentials) for voice
calls — not part of the deployed server. `docker compose up -d livekit`, then run with
`RAINDROP_VOICE_PROVIDER=livekit`; `LIVEKIT_NODE_IP` in `.env` is the address LiveKit advertises for
audio (127.0.0.1 for clients on this machine, the LAN address otherwise).

```bash
cp .env.example .env   # then set real passwords
docker compose up -d postgres      # or: sqlserver
```

Each service bind-mounts its data to `./data/<service>` on the host (gitignored), so `docker compose
down` — even `down -v` — and image updates never lose it; only deleting `./data` does. Point the
server at whichever one is running:

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
