# Architecture overview

## Solution layout

RaindropServer is a `.sln` of seven projects, all targeting `net10.0`:

| Project | Role |
|---|---|
| [`RaindropServer/`](../RaindropServer/) | The library. Hosts an ASP.NET Core app on a background thread, auto-discovers endpoints, and owns the domain layer behind them: users, sessions, password hashing, the member roster and presence, roles and permissions, channels and their messages, and the database abstraction. **No HTTP endpoints live here.** |
| [`RaindropServer.Data.Sqlite/`](../RaindropServer.Data.Sqlite/) | Database module: SQLite driver. |
| [`RaindropServer.Data.Postgres/`](../RaindropServer.Data.Postgres/) | Database module: PostgreSQL driver. |
| [`RaindropServer.Data.SqlServer/`](../RaindropServer.Data.SqlServer/) | Database module: SQL Server driver. |
| [`RaindropServer.Voice.LiveKit/`](../RaindropServer.Voice.LiveKit/) | Media module: carries voice calls on a LiveKit sidecar — tokens, the room API, and the signalling relay. Idle unless `voice.provider` names it. See [Voice](voice.md). |
| [`ServerCli/`](../ServerCli/) | The executable everyone runs. Reads `raindrop.json`, enables one database module, starts the server, hosts the owner's console, and **owns every endpoint**, under `ServerCli/Endpoints/`. |
| [`RaindropServer.Tests/`](../RaindropServer.Tests/) | xUnit tests for the library and the CLI's wiring — services, stores, configuration, attachments, identity and MLS framing. Runs on SQLite, so it needs no database server. See [CI & releases](ci-and-releases.md). |

Each database module is a separate assembly holding one `IDatabaseDriver` implementation and the
client library for that database — see [Database modules](database-modules.md).

## The decentralisation principle

Raindrop is decentralised: every server is independent, and a client holds a **separate GUID per
server**. Nothing in the codebase may assume another server exists — there is no cross-server
directory, no federation, no shared identity.

Every server starts out identical because every owner runs the same CLI. What makes one server
different from another is its address, its users, and implicitly its database and its plugins. This
is why endpoints belong in `ServerCli/Endpoints/` rather than the library: the library is the part
that's the same everywhere, endpoints are the HTTP surface a given deployment exposes.

## Library folder structure

```
RaindropServer/
  ApiException.cs                 — the exception type every endpoint failure is thrown as
  AuthenticatedEndpointAdapter.cs
  TypedEndpointAdapter.cs         — adapters wrapping the typed/authenticated endpoint interfaces
  EndpointResponse.cs             — writes a response through the negotiated IPayloadCodec
  IEndpoint.cs / ITypedEndpoint.cs / IAuthenticatedEndpoint.cs / IAuthenticatedTypedEndpoint.cs
  ServerHost.cs / ServerHostOptions.cs / ServerIdentity.cs
  ServiceCollectionExtensions.cs  — AddRaindropServer and friends (DI wiring)
  Attachments/                    — encrypted files: uploads, storage, policy, cleanup, previews
  Channels/                       — channel domain shared by every channel type
    Text/                         — text-channel-specific domain (messages, reports)
    Voice/                        — voice-channel-specific domain (calls, IVoiceTransport, reconciliation)
  Configuration/                  — IStartupCheck, the configuration report, option names
  Data/                           — IDatabaseDriver contract, registry, Relational* store family
  E2ee/                           — devices, certificates, the link relay, the MLS delivery service
  Events/                         — IEventPublisher, the connection registry, IServerEventHandler (push)
    WebSockets/                   — the WebSocket transport for events
  Members/                        — the roster and presence
  Payloads/                       — IPayloadCodec (JSON / MessagePack)
  Roles/                          — permissions and role management
  Security/                       — TLS certificates, the server identity key, Ed25519 signatures, file permissions
  Users/                          — accounts, sessions, password hashing, retention cleanup
```

`ServerCli/Endpoints/` mirrors this split: `Users/`, `Members/`, `Roles/`, `Events/`, `Attachments/`, `E2ee/`,
`Channels/` (with nested `Channels/Text/` and `Channels/Voice/`), plus a couple of standalone endpoints
(`ServerInfoEndpoint`, `HelloEndpoint`, `GreetJsonEndpoint`).

## Startup sequence

`ServerCli/Program.cs` wires everything up in a fixed order, each step reporting its own errors as a
one-line message rather than a stack trace (the failures here are server-owner typos — a bad
permission name, an unreachable database, a missing certificate — not bugs):

1. **Load configuration** — `ServerConfig.Load()` reads `raindrop.json` next to the executable, then
   applies `RAINDROP_*` environment variable overrides. See [Configuration](configuration.md).
2. **Configure services** — when `voice.provider` is `livekit`, `AddRaindropLiveKit(...)` registers
   the media transport first (the voice subsystem only fills one in when none is registered); then
   `AddRaindropServer(...)` registers the user subsystem, roles, channels, voice, and discovers every
   endpoint automatically.
3. **Enable the database and create missing tables** — `AddRaindropDatabase(...)` then
   `InitializeRaindropDatabaseAsync()`, done *before* the server starts listening so the first request
   can never arrive without a schema, and so the two built-in roles exist before anyone can register.
   This is also where pending [permission rollouts](roles-and-permissions.md#permission-rollouts) are
   applied.
4. **Load or generate the TLS certificate** — see [Transport security](transport-security.md).
5. **Start the HTTP server on a background thread** — `ServerHost.Start(services, hostOptions)` runs
   ASP.NET Core inside `Task.Run`, keeping the calling thread free for the console loop below.
6. **Run the owner's console** on the main thread — see [Configuration](configuration.md#the-owner-console)
   for the available commands. With no TTY attached (a container without one), the process instead
   waits for `SIGINT`/`SIGTERM`.

`ServerHost` is `IAsyncDisposable` and exposes `Services`, so the console (and anything else run
alongside it) acts through the exact same service instances the endpoints use, rather than writing
behind the running server's back.

## Shutdown

Shutdown is bounded by `ServerHostOptions.ShutdownTimeout` — five seconds by default, rather than the
ASP.NET Core host's usual thirty. This matters for Docker: `docker stop` gives a container ten seconds
to exit before sending `SIGKILL`, and a server that took the full thirty seconds to drain a connection
would be killed mid-shutdown, never completing it gracefully at all.

On the way down, `VoiceMaintenanceService` ends every voice call (best-effort, within three seconds of
that budget), and at startup it ends any call a previous process left running on the media server —
every session died with that process, so nothing in those calls can be adopted.

## Where the other two projects fit

This server is one of three repositories (see the [docs index](README.md#the-three-raindrop-projects)).
Its relationship to them is narrow and deliberate:

- **[RaindropCrypto](../../RaindropCrypto/docs/architecture-overview.md)** is never referenced by this
  solution. There is no Rust in the build, no crypto library to link. What the two share is a set of
  byte formats ([wire contracts](../../RaindropCrypto/docs/wire-contracts.md)) that this server parses
  only as far as it must — a device certificate to verify, an `MLSMessage` wrapper to frame-check
  (`E2ee/MlsFraming.cs`) — and never decrypts.
- **[RaindropClient](../../RaindropClient/docs/network-layer.md)** is the only client, so this server's
  endpoint contracts and its `core/network/` wrappers are each other's only compatibility reference.
  A change to a request or response shape here means a matching change there, and in both docs folders.

## Wiring reference

See [Endpoint model & DI wiring](endpoint-model.md) for the full breakdown of `AddRaindropServer`,
`AddRaindropRoles`, `AddRaindropChannels`, `AddRaindropDatabase` and `InitializeRaindropDatabaseAsync`.
