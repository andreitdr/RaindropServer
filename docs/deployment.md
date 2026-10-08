# Deploying to a server

[`deploy.sh`](../deploy.sh) puts the server on a machine you reach over SSH that runs Docker. It exports
a git ref of this repository and one of the sibling [RaindropProto](../../RaindropProto/README.md) (the
wire protocol the server compiles), builds the image **on that machine** with the same
`docker-compose.yml` you use locally
([Local development & Docker](local-development-and-docker.md)), restarts the `server` service, and
checks that the server came back as the same server.

```bash
./deploy.sh                  # deploy HEAD, after showing the plan and asking
./deploy.sh --ref v1.0.2     # any tag, branch or commit; deploying an older one is the rollback
./deploy.sh --dry-run        # check the server and print the plan, change nothing
./deploy.sh --yes            # no confirmation prompt
./deploy.sh --proto-ref v2   # build against another RaindropProto commit than its HEAD
```

The target defaults to `andrei@192.168.0.106`, directory `raindrop` in that user's home. Change it with
`--host`, `--user`, `--dir` or the environment variables `DEPLOY_HOST`, `DEPLOY_USER`, `DEPLOY_DIR`.
`--host` may be a name from `~/.ssh/config`. RaindropProto is read from `../RaindropProto` (`--proto-dir`
or `DEPLOY_PROTO_DIR` to point elsewhere), at its `HEAD` unless `--proto-ref` says otherwise.

## What a deploy does

1. **Exports the ref**, and the RaindropProto ref, with `git archive`. Only committed files travel, so no
   `.env`, `data/` or build output can leak in, and what runs is always traceable to a pair of commits.
   Uncommitted changes in either repository are **not** deployed; the script warns when there are some.
2. **Checks the server** (read-only): Docker usable without `sudo`, Compose v2 present, disk space,
   what is deployed now, whether it answers.
3. **Refuses**, before anything live is touched, a configuration that would do harm — see below.
4. **Builds the image on the server.** A Mac and the server may not share a CPU architecture, and
   building where the image runs avoids emulation and needs no registry. The running server keeps
   serving until the build has succeeded.
5. **Restarts** with `docker compose up -d server`, which also keeps `postgres` and `livekit` up as the
   compose file describes. A service whose configuration did not change is not recreated, so a deploy
   restarts the server and nothing else.
6. **Waits** (up to 120 s) for the new server to answer its handshake. Over SSH it runs `ServerCli probe`
   inside the container: a gRPC `GetServerInfo` call that prints the protocol version, fingerprint and
   identity key as `key=value` lines, so this machine needs nothing but `git`, `ssh` and `sed`. It gives up early once
   the container has kept restarting for 20 s (the server exits when it cannot reach its database and
   leaves the retrying to Docker, so a restart or two on a first start is normal), and prints the
   server's log if it never comes up.
7. **Compares the identity key** the server serves with the one it served before. Clients pin that key
   ([Transport security](transport-security.md#the-server-identity-key-changing-the-certificate-without-breaking-pins)),
   so a different one means `data/server` was empty or not mounted; the deploy is reported as failed
   and is not recorded. A replaced certificate with the same key is only a warning.

   The first deploy after the move to gRPC replaces a server that has no `probe` command. The image
   marks that it has one (`SERVERCLI_HAS_PROBE`), so the script does not run it in an older container.
   It cannot learn the old key this way either, so it warns that the identity check is skipped this
   once. Compare the `Server identity` line in the log by hand afterwards.

### What is refused

Everything found is reported together, and nothing on the server has changed when it is:

- `RAINDROP_DEV_WIPE_DATABASE` is on in the server's `.env` — it would drop every table on each start
  ([Local development & Docker](local-development-and-docker.md#wiping-the-database-on-every-start)).
- `POSTGRES_PASSWORD` is still the `.env.example` placeholder.
- Docker Compose rejects the configuration, for example a required variable missing from `.env`
  (`docker compose config` runs against the new compose file and the server's `.env`).
- `.env` is missing but `data/postgres` exists: the database's password came from the `.env` that is
  gone, and a newly generated one would lock the server out of its own data.
- A `docker-compose.override.yml` that `deploy.sh` did not write is in the way.

## Layout on the server

| Path (under the deploy directory) | What it is | A deploy |
|---|---|---|
| `.env` | Passwords and settings, mode 600 | Created once, never overwritten, never copied off the machine |
| `data/` | The database, the TLS certificate and identity key, the encrypted attachments | **Never touched** |
| `src/` | The exported source; the Docker build context | Replaced wholesale |
| `proto/` | The exported RaindropProto; the build's second context (`raindrop_proto`) | Replaced wholesale |
| `docker-compose.yml`, `livekit.yaml` | Copied from the export | Replaced |
| `docker-compose.override.yml` | Written by the script; points the build at `src/` and `proto/` | Rewritten |
| `.deployed`, `deploy.log` | The last release that came up healthy (server and protocol commits), and the history of them | Updated only after a healthy deploy |

`src/` is deliberately not the directory that holds `data/`. On Linux, Postgres's data directory is
unreadable to ordinary users, and a `docker build` whose context contains it fails with *permission
denied*; Docker Desktop hides this, which is why it does not show up locally. Run `docker compose`
commands by hand from the deploy directory (`cd raindrop && docker compose logs -f server`).

## First deploy

The server needs Docker with the Compose v2 plugin and a user that may run `docker` without `sudo`
(`sudo usermod -aG docker <user>`, then log in again). Install an SSH key so the script does not ask for
a password (it asks once per run either way; host keys are always verified):

```bash
ssh-keygen -t ed25519            # if you have no key yet
ssh-copy-id andrei@192.168.0.106
```

With no `.env` in the deploy directory, the first deploy creates one, and the plan says so before you
confirm. It holds generated passwords (read from `/dev/urandom`, so no `openssl` is needed on the server,
and none is printed),
`RAINDROP_DEV_WIPE_DATABASE=false`, `LIVEKIT_NODE_IP` set to `--host` when that is an IPv4 address (the
address LiveKit tells clients to send audio to — anything else, including a name, gets `127.0.0.1` and a
warning, and voice then only works on the server itself), and Postgres bound to `127.0.0.1` rather than
published to the network, since only the server container talks to it. Back `.env` up with `data/`:
`data/postgres` is initialised with that password.

Open these ports to the clients, if the server has a firewall: `5001/tcp` (the API, the event stream and
the voice signalling relay), `7881/tcp` and `7882/udp` (LiveKit's media).

The first build pulls about 1 GB of base images; later ones reuse the layer cache. The server's
certificate and identity key are generated when it first starts, so a client that connects after the
first deploy pins them from then on.

The image runs as a non-root user, and Docker creates a missing `data/server` owned by root, which on
Linux leaves the server unable to write its certificate and key. The deploy fixes the owner from inside a
throwaway container (no `sudo`); only entries with the wrong owner are touched.

## Rolling back

`.deployed` names the last release that came up healthy, and a failed deploy prints the command:

```bash
./deploy.sh --ref <that commit>
```

That is the same code path as any deploy, and the layer cache makes it quick. It redeploys older code
against the **current** database. The schema only ever gains tables ([Database modules](database-modules.md)),
so older code generally ignores what it does not know, but nothing checks that: take a database dump
first when a release changes what is stored.

## Backups

Back up three things together: `.env`, `data/server` (**the identity key and certificate** — losing the
key locks out every client that pinned it — and the attachments) and the database. Copy Postgres with a
dump rather than its live directory:

```bash
ssh andrei@192.168.0.106 'cd raindrop && docker compose exec -T postgres sh -c '"'"'pg_dump -U "$POSTGRES_USER" "$POSTGRES_DB"'"'"'' > raindrop.sql
```

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `Host key verification failed` | First connection from this machine. Run `ssh andrei@192.168.0.106` once, check the fingerprint, accept it. |
| `docker is not usable … permission denied` | The user is not in the `docker` group; see First deploy. |
| The build is killed or very slow | The server is short on memory. Add swap, or free some, and deploy again — the layer cache keeps the finished steps. |
| `port is already allocated` | Something else on the server uses 5001, 7881 or 7882. Set `RAINDROP_PORT` in the server's `.env` for the first; the LiveKit ports are fixed in `docker-compose.yml`. |
| The new server does not come up | The script prints its log; the startup validation report ([Configuration](configuration.md#startup-validation)) names what is wrong. |
| Two deploys at once | The second stops with *another deploy is already running* (where `flock` exists). |
| The connection dropped mid-deploy | Run it again; every step is safe to repeat. |
