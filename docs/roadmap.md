# Roadmap

What RaindropServer does today, and what it does not do yet. This file is the honest inventory for
the **server**; the other two projects keep their own, and a feature usually needs all three to be
useful end to end:

- [RaindropCrypto roadmap](../../RaindropCrypto/docs/roadmap.md) — the Rust E2EE core.
- [RaindropClient roadmap](../../RaindropClient/docs/roadmap.md) — the Flutter app.

**Keep this honest.** When something here ships, move it from *Not implemented yet* into
*Implemented* in the same change that ships it, and update the doc it points at.

## Implemented

| Area | What works | Doc |
|---|---|---|
| Hosting | ASP.NET Core on Kestrel hosted in-process by a console app, the owner console, bounded shutdown | [Architecture](architecture-overview.md) |
| API | Every client call over gRPC (`raindrop.v1`, defined in RaindropProto): interceptor-resolved sessions, error statuses with `ErrorInfo` reasons, streamed uploads and downloads | [gRPC services](grpc-services.md) |
| Accounts | Registration, login, logout, sessions, username/password changes, username availability, user lookup, leaving and rejoining, PBKDF2 hashing, retention cleanup of left accounts | [Users](users.md) |
| Roster | Members with presence (online while an event stream is open, offline a minute after its heartbeats stop), presence broadcast | [Members](members.md) |
| Roles | Role CRUD, assignment, the 13-permission catalogue, built-in `everyone`/`owner`, permission rollouts, the in-memory permission cache | [Roles & permissions](roles-and-permissions.md) |
| Channels | Text and voice channels, create/rename/delete, `IChannelModule` dispatch, name policy | [Channels](channels.md) |
| Messages | Send, history paging by cursor, deletion with tombstones, moderator deletion, archived copies | [Messages](messages.md) |
| Encrypted messages | `SendEncryptedMessage` for MLS ciphertext, plus the report path that proves a reported message's text with the author's attestation | [Messages](messages.md#reporting-an-encrypted-message) |
| Attachments | Streamed, resumable uploads and downloads, owner-set size/type rules, local-folder storage, orphan cleanup, encrypted thumbnails, cancel | [Attachments](attachments.md) |
| Voice | Group calls on a LiveKit sidecar, join/leave, mute/deafen, server-wide mute for everyone, the signalling relay, permission watching, startup/shutdown reconciliation, SFrame-encrypted media (`e2ee.voice.v1`) | [Voice](voice.md) |
| Camera video | The `video` permission, camera self-state, a per-call cap on simultaneous cameras enforced at the media server, and the publishing grant that follows it (`voice.video.v1`) | [Voice](voice.md#camera-video) |
| E2EE, identity | Identity keys, device certificates, device registration/listing/revocation, identity reset, session binding, sealed key backup, the device-link relay (`e2ee.devices.v1`) | [E2EE](e2ee.md) |
| E2EE, delivery | The MLS delivery service: groups, one-commit-per-epoch, group info, key packages, Welcomes, roster, sync (`e2ee.delivery.v1`) | [E2EE](e2ee.md#the-delivery-service) |
| Push | The bidirectional `EventService.Connect` stream with every event a member may see, heartbeats, resubscribe, plugin events, publisher/transport/handler seams | [Events](events.md) |
| Databases | SQLite, PostgreSQL and SQL Server as separately loaded driver assemblies, schema creation on startup | [Database modules](database-modules.md) |
| Wire format | Protobuf only; the LiveKit room API is binary Twirp too | [Wire format](wire-formats.md) |
| Transport | TLS always (no plain-HTTP mode), self-signed by default, fingerprint pinning, a P-256 server identity key | [Transport security](transport-security.md) |
| Configuration | `raindrop.json`, `RAINDROP_*` overrides, startup checks, the owner console | [Configuration](configuration.md) |
| Deployment | Docker image, Compose stack with Postgres/SQL Server and LiveKit, `deploy.sh` over SSH with backups and rollback | [Local development & Docker](local-development-and-docker.md), [Deployment](deployment.md) |
| CI | Build + test on every push to `main`, self-contained packages for Linux/macOS/Windows on a version tag | [CI & releases](ci-and-releases.md) |

End-to-end encrypted text messages and calls now work all the way through to users: the client drives
each channel's MLS group through this delivery service. See
[RaindropClient's messages doc](../../RaindropClient/docs/messages-chat.md#end-to-end-encryption).

## Not implemented yet

### Chat
- **Message editing.** Messages can only be sent and deleted.
- **Reactions, threads, pinned messages, typing indicators, read state / unread counts, mentions and
  server-side search.** None of these exist in the schema or the API.
- **Direct messages.** Every conversation is a channel on a server.
- **Per-channel permission overrides.** Permissions are resolved server-wide;
  `RolePermissionResolver` is the single seam where overrides would go, and nothing outside it would
  change. [Roles & permissions](roles-and-permissions.md)
- **Invites, bans and kicks.** Joining is registration, gated by `firstMemberBecomesOwner` and the
  `everyone` role; there is no way to remove a member other than their own `leave`.
- **Avatars and profiles.** A member is a username and a GUID.

### Voice
- **Screen sharing.** Camera video shipped; the LiveKit token's `canPublishSources` now names
  `microphone` and `camera`, and widening it again to `screen_share` is the change. The per-call cap
  and the grant-follows-self-state handshake are already in place to copy.
  [Voice](voice.md#camera-video)
- **Voice transports other than LiveKit.** `IVoiceTransport` is the seam, and LiveKit is the only
  implementation; without one registered, voice channels report themselves unavailable.

### Moderation of encrypted content
- **Revealing a reported message's files.** A report proves the text; the attachments on a reported
  message cannot be revealed to moderators yet. [Attachments](attachments.md)

### Protocol and storage
- **Schema migrations.** `EnsureSchemaAsync` only ever creates what is missing — it never alters an
  existing table. Permissions are a `long` and presence/attachment states are text precisely so the
  usual reasons to migrate do not arise, but changing an existing column still means doing it by
  hand. [Database modules](database-modules.md)
- **Event replay / resume.** A client that reconnects gets `ready` and re-reads state; nothing is
  replayed from a cursor. [Events](events.md)
- **`EventOptions` in `raindrop.json`.** They are passed as `configureEvents` in code only.
- **Attachment storage other than a local folder.** `IAttachmentStorage` is the seam; object storage
  would be a new implementation.

### Operations
- **Rate limiting, metrics and a health check.** None of these exist. A reverse proxy cannot add them
  either, since it has to pass TLS through untouched. `ServerCli probe` is the nearest thing to a health
  check. A gRPC health service (`grpc.health.v1`) would be the natural one.
- **Automated backups.** `deploy.sh` snapshots before a deploy; scheduled backups are the owner's
  job. [Deployment](deployment.md)

## Deliberately not planned

- **Federation, a central directory, or any cross-server identity.** Raindrop is decentralised in the
  strong sense: each server is independent and nothing in the codebase may assume another server
  exists. A client holds a separate GUID per server. See
  [Architecture overview](architecture-overview.md#the-decentralisation-principle).
- **Server-readable message content as the default.** New message features are expected to work with
  ciphertext the server cannot read; the report flow is the model for anything moderation needs.
