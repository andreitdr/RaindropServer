# RaindropServer documentation

RaindropServer is the self-hosted backend for Raindrop, a decentralised chat platform: every server
is an independent, owner-run instance with its own users, channels and database — there is no central
directory and no assumption that another server exists anywhere.

This wiki documents the feature set as it stands today. Each file covers one area of the system; start
with [Architecture overview](architecture-overview.md) if you're new to the codebase.

## Contents

| Doc | Covers |
|---|---|
| [Architecture overview](architecture-overview.md) | Solution layout, the five projects, how they depend on each other |
| [Endpoint model & DI wiring](endpoint-model.md) | The four endpoint interfaces, auto-discovery, `AddRaindropServer` and friends |
| [Wire formats](wire-formats.md) | JSON vs. MessagePack negotiation |
| [Transport security](transport-security.md) | TLS, self-signed certificates, fingerprint pinning |
| [End-to-end encryption](e2ee.md) | Identity keys, devices, session binding, key backup and device linking, and the MLS delivery service (groups, commits, key packages, Welcomes) — the server side of E2EE |
| [Users](users.md) | Registration, login, sessions, leaving/rejoining, password hashing, User API |
| [Members](members.md) | The roster and presence, Member API |
| [Roles & permissions](roles-and-permissions.md) | The permission model, built-in roles, who may change what, Role API |
| [Channels](channels.md) | Channel types, `IChannelModule`, names, permissions, Channel API |
| [Messages](messages.md) | Text messages, history paging, deletion/tombstones, Message API |
| [Voice](voice.md) | Voice channels: group calls on a LiveKit sidecar, mute/deafen, mute for everyone, the signalling relay, Voice API |
| [Events (push)](events.md) | The `/api/events` WebSocket, every event type, close codes, and the publisher/transport/handler seams plugins build on |
| [Database modules](database-modules.md) | `IDatabaseDriver`, the relational store family, schema, adding a driver |
| [Configuration](configuration.md) | `raindrop.json`, environment variables, the owner console |
| [Local development & Docker](local-development-and-docker.md) | Running against Postgres/SQL Server locally, the Docker image |

## Quick facts

- **Stack:** ASP.NET Core (Kestrel) hosted in-process by a plain console app, targeting `net10.0`.
- **Databases supported today:** SQLite (default), PostgreSQL, SQL Server — each a separate,
  independently loaded assembly.
- **Transport:** HTTPS only, self-signed by default with trust-on-first-use fingerprint pinning (same
  model as SSH host keys).
- **Wire formats:** JSON (default) and MessagePack, negotiated per request via `Content-Type`/`Accept`.
- **Push:** a WebSocket at `GET /api/events` delivers every change a member may see, as it happens.
- **What's implemented:** accounts, sessions, roles/permissions, text channels and messages, voice
  channels (audio on a LiveKit sidecar), push events.
- **End-to-end encryption:** both the identity layer (identity keys, devices, session binding, key
  backup, device linking) and the MLS delivery service (groups, commits, key packages, Welcomes) are
  implemented server-side; see [E2EE](e2ee.md). Encrypted text messages and calls don't reach users yet
  — the client's `E2eeApi` wrapper is wired and tested against the delivery service, but nothing in the
  app drives a channel's group through it yet.
- **What's not implemented yet:** camera and screen sharing in voice channels, per-channel permission
  overrides, the app actually sending end-to-end encrypted messages and calls (the server and Rust core
  are ready; see [RaindropCrypto's docs](../../RaindropCrypto/docs/README.md#quick-facts)), schema
  migrations, event replay/resume (a reconnecting client re-reads instead).

## Keeping this wiki current

**Update the relevant file(s) here whenever a feature changes or a new one is added** — a new
endpoint, a changed contract, a new permission, a new database driver, a new config option. Treat this
as part of the change, not a follow-up: a docs folder that drifts from the code is worse than no docs
folder, because it actively misleads. When a change doesn't fit any existing file, add a new one and
link it from the table above.
