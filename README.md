# RaindropServer

The self-hosted backend for **Raindrop**, a decentralised chat platform. Every server is an
independent, owner-run instance with its own users, channels and database: there is no central
directory, no federation, and nothing in the code may assume another server exists.

ASP.NET Core (Kestrel) hosted in-process by a console app, targeting `net10.0`. HTTPS only,
self-signed by default with trust-on-first-use fingerprint pinning. SQLite, PostgreSQL or SQL Server,
each a separately loaded driver assembly. Messages, files and call media are **end-to-end
encrypted** — the server stores and relays bytes it cannot read.

## The three Raindrop projects

Checked out side by side in one folder:

| Project | What it is |
|---|---|
| **RaindropServer** (this repo) | The backend: accounts, roles, channels, messages, attachments, voice, push events, and the MLS delivery service |
| [`../RaindropCrypto`](../RaindropCrypto) | The end-to-end encryption core: Rust, MLS (RFC 9420) over OpenMLS. Runs on clients only — **this server never links it and never holds a private key** |
| [`../RaindropClient`](../RaindropClient) | The Flutter app (macOS, Windows, Linux, iOS, Android), which compiles RaindropCrypto into itself |

The server and the core never share code, only byte formats
([wire contracts](../RaindropCrypto/docs/wire-contracts.md) ·
[the server's side of them](docs/e2ee.md)). The client is this server's only client, so their
contracts are each other's only compatibility reference.

## Documentation

**[`docs/`](docs/README.md) is the reference wiki** — one file per area, kept current with the code.
Start with:

- [Architecture overview](docs/architecture-overview.md) — the seven projects and how they fit
- [Roadmap](docs/roadmap.md) — what is implemented, what is not, what is not planned
- [Configuration](docs/configuration.md) — `raindrop.json`, environment variables, the owner console
- [Local development & Docker](docs/local-development-and-docker.md) — running the whole stack
- [Deployment](docs/deployment.md) — `deploy.sh` over SSH, backups, rollback
- [CI & releases](docs/ci-and-releases.md) — the GitHub Actions workflows

[`CLAUDE.md`](CLAUDE.md) is the condensed guide for coding agents working in this repository.

## Quick start

```bash
dotnet run --project ServerCli
```

That starts on `https://localhost:5001` with SQLite and a generated self-signed certificate, and
drops you into the owner console (`help` lists its commands). The first UUID to register owns the
server.

```bash
dotnet test
```

The whole stack — server, Postgres and a LiveKit media server for voice — comes up with Docker:

```bash
cp .env.example .env   # then set POSTGRES_PASSWORD and LIVEKIT_API_SECRET
docker compose up -d server
```

## Keeping the docs current

Changing a feature means changing its doc in the same commit: a new endpoint, a changed contract, a
new permission, a new database driver, a new config option. A `docs/` folder that drifts from the code
is worse than none, because it misleads. If the change crosses a project boundary, update the other
project's docs too.
