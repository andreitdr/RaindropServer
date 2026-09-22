# Local development & Docker

## Running the CLI directly

From the repository root:

```bash
dotnet build
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

The server presents a self-signed certificate by default (see
[Transport security](transport-security.md)), so `curl` needs `-k` until you trust it:

```bash
curl -k https://localhost:5001/api/server/info
```

```bash
curl -k -X POST https://localhost:5001/api/users/register \
  -H "Content-Type: application/json" \
  -d '{"userId":"<uuid>","username":"alice","password":"correct horse"}'
```

Authenticated calls carry the token returned by registering or logging in:

```bash
curl -k https://localhost:5001/api/members -H "Authorization: Bearer <token>"
```

The first UUID to register owns the server (see
[Roles & permissions](roles-and-permissions.md#the-two-built-in-roles)), so its token is the one that
can create roles:

```bash
curl -k -X POST https://localhost:5001/api/roles/create \
  -H "Content-Type: application/json" -H "Authorization: Bearer <owner token>" \
  -d '{"name":"Moderator","permissions":["manage_messages","rename_channels"]}'
```

See [Users](users.md), [Channels](channels.md) and [Messages](messages.md) for more example calls, and
[Configuration](configuration.md#the-owner-console) for the console commands available once it's
running.

## Local databases via Docker

`docker-compose.yml` runs Postgres and SQL Server locally so you can develop against a real database
instead of the default SQLite. This is **not** part of the deployed server image, and isn't needed at
all when running against `sqlite`.

```bash
cp .env.example .env   # then set real passwords
docker compose up -d postgres      # or: sqlserver
```

Each service bind-mounts its data to `./data/<service>` on the host (gitignored), so `docker compose
down` — even `down -v` — and image updates never lose it; only deleting `./data` does.

Point the server at whichever one is running:

```bash
RAINDROP_DB_PROVIDER=postgres \
RAINDROP_DB_CONNECTION_STRING="Host=localhost;Port=5432;Database=raindrop;Username=raindrop;Password=<POSTGRES_PASSWORD>" \
dotnet run --project ServerCli
```

```bash
RAINDROP_DB_PROVIDER=sqlserver \
RAINDROP_DB_CONNECTION_STRING="Server=localhost,1433;Database=raindrop;User Id=sa;Password=<MSSQL_SA_PASSWORD>;TrustServerCertificate=True" \
dotnet run --project ServerCli
```

The SQL Server image is amd64-only; on Apple Silicon it runs under emulation
(`platform: linux/amd64` in the compose file) — slow but correct.
`mcr.microsoft.com/azure-sql-edge` is a native-arm64 alternative that speaks the same TDS protocol, if
you'd rather swap images than eat the emulation cost.

## LiveKit, for voice channels

Voice calls need a LiveKit media server beside Raindrop ([Voice](voice.md)). The same compose file
runs one in LiveKit's `--dev` mode, whose key and secret (`devkey` / `secret`) match the defaults in
`raindrop.json`:

```bash
docker compose up -d livekit
```

```bash
RAINDROP_VOICE_PROVIDER=livekit dotnet run --project ServerCli
```

Without Docker, `brew install livekit` and then `livekit-server --dev --bind 0.0.0.0` does the same.

`LIVEKIT_NODE_IP` in `.env` is the address LiveKit tells clients to send audio to — Docker cannot work
it out from inside the container. `127.0.0.1` is right when every client runs on this machine; use this
machine's LAN address when a phone or another computer joins. Port 7880 (the API and signalling) is
published on loopback only: clients reach it through Raindrop's relay, never directly. 7881/TCP and
7882/UDP carry the media and are what clients connect to.

The LiveKit CLI (`lk`, from `brew install livekit-cli`) is handy for testing without a second client:
`lk room list` and `lk room participants list <room>` against `--url http://localhost:7880 --api-key
devkey --api-secret secret` show what LiveKit thinks is going on, which is what Raindrop's reconciler
compares itself with.

## Building the Docker image

[`ServerCli/Dockerfile`](../ServerCli/Dockerfile) is built from the **repository root** as context,
since it references `../.dockerignore`:

```bash
docker build -f ServerCli/Dockerfile -t raindrop-server .
```

Configure the running container entirely through the `RAINDROP_*` environment variables in
[Configuration](configuration.md#environment-variable-overrides) — there's no need to bake or mount a
`raindrop.json` unless you want to.

Two things that matter for containers specifically, both covered in more depth elsewhere:

- **The TLS certificate must live on a mounted volume** (see
  [Transport security](transport-security.md#two-consequences)) — an ephemeral container filesystem
  would regenerate it, and a new fingerprint, on every restart.
- **Shutdown is bounded to five seconds** by `ServerHostOptions.ShutdownTimeout` (see
  [Architecture overview](architecture-overview.md#shutdown)), well inside the ten seconds `docker
  stop` allows before escalating to `SIGKILL`.
- A container started **without a TTY** skips the interactive console and waits for
  `SIGINT`/`SIGTERM` instead (see [Configuration](configuration.md#the-owner-console)).
