# RaindropServer documentation

RaindropServer is the self-hosted backend for Raindrop, a decentralised chat platform: every server
is an independent, owner-run instance with its own users, channels and database — there is no central
directory and no assumption that another server exists anywhere.

This wiki documents the feature set as it stands today. Each file covers one area of the system; start
with [Architecture overview](architecture-overview.md) if you're new to the codebase, and
[Roadmap](roadmap.md) for what is and is not built yet.

## The three Raindrop projects

Raindrop is four repositories, checked out side by side in one folder:

| Project | What it is | Its docs |
|---|---|---|
| **RaindropServer** (this one) | The self-hosted backend: ASP.NET Core, one owner-run instance per server | you are here |
| [RaindropProto](../../RaindropProto/README.md) | The wire protocol: every gRPC service and protobuf message a client and a server exchange. This server compiles it on every build | [docs](../../RaindropProto/docs/README.md) |
| [RaindropCrypto](../../RaindropCrypto/docs/README.md) | The end-to-end encryption core: Rust, MLS (RFC 9420) over OpenMLS. Runs only on clients — **this server never links it and never holds a private key** | [docs](../../RaindropCrypto/docs/README.md) |
| [RaindropClient](../../RaindropClient/docs/README.md) | The Flutter app for macOS, Windows, Linux, iOS and Android; compiles RaindropCrypto into itself | [docs](../../RaindropClient/docs/README.md) |

The server's part in encryption is to carry bytes it cannot read: it stores ciphertext, runs the MLS
delivery service, and relays device links. The formats it has to agree with the core on are listed in
[RaindropCrypto's wire contracts](../../RaindropCrypto/docs/wire-contracts.md); the server side of the
same formats is [E2EE](e2ee.md).

## Contents

| Doc | Covers |
|---|---|
| [Architecture overview](architecture-overview.md) | Solution layout, the seven projects, how they depend on each other |
| [gRPC services & DI wiring](grpc-services.md) | The `raindrop.v1` services, authentication, how errors arrive, `IRouteContributor`, `AddRaindropServer`/`AddRaindropGrpc` and friends |
| [Wire format](wire-formats.md) | Protobuf over gRPC, and the three things that are not |
| [Transport security](transport-security.md) | TLS, self-signed certificates, fingerprint pinning |
| [End-to-end encryption](e2ee.md) | Identity keys, devices, session binding, key backup and device linking, and the MLS delivery service (groups, commits, key packages, Welcomes) — the server side of E2EE |
| [Users](users.md) | Registration, login, sessions, leaving/rejoining, password hashing, User API |
| [Members](members.md) | The roster and presence, Member API |
| [Roles & permissions](roles-and-permissions.md) | The permission model, built-in roles, who may change what, Role API |
| [Channels](channels.md) | Channel types, `IChannelModule`, names, permissions, Channel API |
| [Messages](messages.md) | Text messages, history paging, deletion/tombstones, Message API |
| [Attachments](attachments.md) | End-to-end encrypted files in messages: streamed, resumable uploads, what the owner's size and type rules are enforced on, storage, cleanup, previews, Attachment API |
| [Voice](voice.md) | Voice channels: group calls on a LiveKit sidecar, mute/deafen, mute for everyone, the signalling relay, Voice API |
| [Events (push)](events.md) | The bidirectional `EventService.Connect` stream, heartbeats, every event, how a stream ends, and the publisher/transport/handler seams plugins build on |
| [Database modules](database-modules.md) | `IDatabaseDriver`, the relational store family, schema, adding a driver |
| [Configuration](configuration.md) | `raindrop.json`, environment variables, the owner console |
| [Local development & Docker](local-development-and-docker.md) | Running the whole stack (server, database, LiveKit) with Docker Compose, developing against Postgres/SQL Server locally, the Docker image |
| [Deploying to a server](deployment.md) | `deploy.sh`: putting the server on a Docker host over SSH — what a deploy does and refuses, the layout on the server, first deploy, rolling back, backups |
| [CI & releases](ci-and-releases.md) | The GitHub Actions workflows: build/test on `main`, self-contained packages on a version tag |
| [Roadmap](roadmap.md) | Everything implemented today, everything that is not, and what is deliberately not planned |

## Quick facts

- **Stack:** ASP.NET Core (Kestrel) hosted in-process by a plain console app, targeting `net10.0`.
- **Databases supported today:** SQLite (default), PostgreSQL, SQL Server — each a separate,
  independently loaded assembly.
- **Transport:** TLS always, self-signed by default with trust-on-first-use fingerprint pinning (same
  model as SSH host keys).
- **API:** gRPC and protobuf only, defined in [RaindropProto](../../RaindropProto/README.md). No REST, no
  JSON. The one non-gRPC route is the voice signalling relay, which carries LiveKit's own protocol.
- **Push:** a bidirectional gRPC stream delivers every change a member may see, as it happens; its
  heartbeats are what presence is made of.
- **What's implemented:** accounts, sessions, roles/permissions, text channels and messages, end-to-end
  encrypted file attachments, voice channels (audio on a LiveKit sidecar, media end-to-end encrypted),
  push events, and the full E2EE server side — identities, device linking, key backup and the MLS
  delivery service.
- **End-to-end encryption:** working end to end. The server holds identity and device records, the
  sealed key backup, group state and ciphertext, and never a private key or a plaintext message. The
  client drives each channel's MLS group through the delivery service, so text messages and call media
  are encrypted for real today — see [E2EE](e2ee.md) and
  [RaindropClient's messages doc](../../RaindropClient/docs/messages-chat.md#end-to-end-encryption).
  A server advertises `e2ee.devices.v1`, `e2ee.delivery.v1` and `e2ee.voice.v1` so a client can tell
  what it supports.
- **What's not implemented yet:** message editing, reactions, threads, read state, direct messages,
  invites/bans, screen sharing in voice channels, per-channel permission overrides, schema
  migrations, and event replay/resume (a reconnecting client re-reads instead). The full list, with
  the seams each one would go through, is in [Roadmap](roadmap.md).

## Keeping this wiki current

**Update the relevant file(s) here whenever a feature changes or a new one is added** — a new
RPC, a changed contract, a new permission, a new database driver, a new config option. Treat this
as part of the change, not a follow-up: a docs folder that drifts from the code is worse than no docs
folder, because it actively misleads. When a change doesn't fit any existing file, add a new one and
link it from the table above.

**[Roadmap](roadmap.md) is part of that.** Anything that moves between implemented and not goes there
in the same change, and a gap that crosses a project boundary is recorded in the other project's
roadmap too ([RaindropCrypto](../../RaindropCrypto/docs/roadmap.md) ·
[RaindropClient](../../RaindropClient/docs/roadmap.md)).

If a change affects the API this server's only client speaks, it is a change to
[RaindropProto](../../RaindropProto/CLAUDE.md) first; update the matching
[RaindropClient doc](../../RaindropClient/docs/README.md) too. If it touches a crypto byte format,
update [RaindropCrypto's wire contracts](../../RaindropCrypto/docs/wire-contracts.md) as well.
