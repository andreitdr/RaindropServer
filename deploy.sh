#!/usr/bin/env bash
# Deploys RaindropServer to a Docker host over SSH: exports a git ref, builds the image on the server
# with docker compose, restarts the stack, and checks that the server came back as the same server.
#
# Usage:
#   ./deploy.sh                 # deploy HEAD (what is committed), after showing the plan and asking
#   ./deploy.sh --ref v1.0.2    # deploy any tag, branch or commit; deploying an older one is the rollback
#   ./deploy.sh --dry-run       # check the server and show the plan, change nothing
#   ./deploy.sh --yes           # skip the confirmation
#
# Where to (flags, or the environment variables in brackets):
#   --host <address>   the server                      [DEPLOY_HOST, default 192.168.0.106]
#   --user <name>      the SSH user                    [DEPLOY_USER, default andrei]
#   --dir <path>       the deploy directory there; relative paths start in that user's home
#                                                      [DEPLOY_DIR, default raindrop]
#
# The wire protocol the server compiles comes from the sibling RaindropProto checkout:
#   --proto-ref <ref>  the RaindropProto commit to build against        [default HEAD]
#   --proto-dir <path> where that checkout is   [DEPLOY_PROTO_DIR, default ../RaindropProto]
#
# How it works:
#   1. `git archive` exports the ref, and the RaindropProto ref beside it. Only committed files travel,
#      so no .env, data/ or build output can leak in, and what runs on the server is always traceable
#      to a pair of commits.
#   2. The image is built ON the server by docker compose. A Mac and the server may not share a CPU
#      architecture, and building where the image runs sidesteps emulation and needs no registry. The
#      running server keeps serving until the build has succeeded.
#   3. `docker compose up -d server` swaps the container (which also keeps postgres and livekit up, as
#      docker-compose.yml describes). Nothing is recreated that did not change.
#   4. The new server is asked for its handshake (`ServerCli probe`, a gRPC GetServerInfo call made
#      inside its container) until it answers, and its identity key is compared with the one it had
#      before: clients pin that key, and a different one means data/server was lost or not mounted.
#
# On the server, under the deploy directory:
#   .env        passwords and settings. Created on the first deploy with generated secrets, never
#               overwritten and never copied off the machine.
#   data/       everything that must outlive a deploy: the database, the TLS certificate, the identity key
#               and the encrypted attachments. A deploy never lists, moves or deletes anything in it.
#   src/        the exported source and the docker build context, replaced wholesale on every deploy.
#               It has to be separate from data/: on Linux Postgres's data directory is unreadable to
#               ordinary users, which fails a `docker build` whose context contains it.
#   proto/      the exported RaindropProto, the build's second context (raindrop_proto); replaced too.
#   docker-compose.yml, livekit.yaml    copied from the export
#   docker-compose.override.yml         written by this script; points the build at src/ and proto/
#
# Refused before anything is touched: a server .env that turns on RAINDROP_DEV_WIPE_DATABASE (it would
# drop every table on each start) or still has the .env.example database password, a compose
# configuration Docker rejects, and a missing .env next to an existing database (a new password would
# lock the server out of it). Every problem found is reported together.

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

HOST="${DEPLOY_HOST:-192.168.0.106}"
SSH_USER="${DEPLOY_USER:-andrei}"
REMOTE_DIR="${DEPLOY_DIR:-raindrop}"
REF="HEAD"
DRY_RUN=false
ASSUME_YES=false
PROTO_REF="HEAD"
PROTO_DIR="${DEPLOY_PROTO_DIR:-}"
HEALTH_TIMEOUT=120   # seconds to wait for the new server to answer
CRASH_LOOP_GRACE=20  # seconds of restarting tolerated before giving up early

log()  { printf '\n\033[1;34m==>\033[0m \033[1m%s\033[0m\n' "$1"; }
ok()   { printf '\033[1;32m✓\033[0m %s\n' "$1"; }
warn() { printf '\033[1;33m!\033[0m %s\n' "$1" >&2; }
fail() { printf '\033[1;31m✗\033[0m %s\n' "$1" >&2; }
die()  { fail "$1"; exit 1; }

usage() {
    cat <<EOF
Usage: ./deploy.sh [--ref <tag|branch|commit>] [--dry-run] [--yes] [--host <address>] [--user <name>] [--dir <path>]
                   [--proto-ref <tag|branch|commit>] [--proto-dir <path>]

Deploys the committed state of this repository (HEAD unless --ref says otherwise), built against the
committed state of ../RaindropProto (HEAD unless --proto-ref says otherwise), to
$SSH_USER@$HOST:$REMOTE_DIR. See the comment at the top of this file for what happens there.
EOF
}

value_of() { [ "$#" -ge 2 ] || die "$1 needs a value"; }

while [ $# -gt 0 ]; do
    case "$1" in
        --ref)     value_of "$@"; REF="$2"; shift 2 ;;
        --host)    value_of "$@"; HOST="$2"; shift 2 ;;
        --user)    value_of "$@"; SSH_USER="$2"; shift 2 ;;
        --dir)     value_of "$@"; REMOTE_DIR="$2"; shift 2 ;;
        --proto-ref) value_of "$@"; PROTO_REF="$2"; shift 2 ;;
        --proto-dir) value_of "$@"; PROTO_DIR="$2"; shift 2 ;;
        --dry-run) DRY_RUN=true; shift ;;
        --yes|-y)  ASSUME_YES=true; shift ;;
        -h|--help) usage; exit 0 ;;
        *)         die "unknown argument '$1' (try --help)" ;;
    esac
done

# Everything below ends up inside ssh command lines, so accept only plain values.
REMOTE_DIR="${REMOTE_DIR#\~/}"
[[ "$HOST" =~ ^[A-Za-z0-9._-]+$ ]]              || die "--host '$HOST' is not a plain host name or address"
[[ "$SSH_USER" =~ ^[a-z_][a-z0-9_-]*$ ]]        || die "--user '$SSH_USER' is not a plain user name"
[[ "$REMOTE_DIR" =~ ^/?[A-Za-z0-9._/-]+$ ]]     || die "--dir '$REMOTE_DIR' contains characters other than letters, digits and . _ / -"
[[ "$REMOTE_DIR" != *..* ]]                     || die "--dir '$REMOTE_DIR' must not contain '..'"
[[ "$REF" =~ ^[A-Za-z0-9._/@^~-]+$ ]]           || die "--ref '$REF' is not a plain tag, branch or commit name"
[[ "$PROTO_REF" =~ ^[A-Za-z0-9._/@^~-]+$ ]]     || die "--proto-ref '$PROTO_REF' is not a plain tag, branch or commit name"

for tool in git ssh sed; do
    command -v "$tool" >/dev/null 2>&1 || die "'$tool' is required but was not found on this machine"
done

# ---- What is being deployed --------------------------------------------------------------------------

git -C "$ROOT_DIR" rev-parse --git-dir >/dev/null 2>&1 || die "$ROOT_DIR is not a git repository"
SHA="$(git -C "$ROOT_DIR" rev-parse --verify --quiet "$REF^{commit}")" \
    || die "'$REF' is not a commit, tag or branch of this repository"
SHORT="$(git -C "$ROOT_DIR" rev-parse --short=7 "$SHA")"
SUBJECT="$(git -C "$ROOT_DIR" log -1 --format=%s "$SHA")"
BRANCH="$(git -C "$ROOT_DIR" branch --show-current)"

if [ "$SHA" = "$(git -C "$ROOT_DIR" rev-parse HEAD)" ] && [ -n "$(git -C "$ROOT_DIR" status --porcelain)" ]; then
    warn "the working tree has uncommitted changes; they are NOT part of this deploy, only commit $SHORT is"
fi

# The wire protocol: RaindropServer.Grpc compiles it from the sibling checkout, so it travels too.
PROTO_DIR="${PROTO_DIR:-$ROOT_DIR/../RaindropProto}"
git -C "$PROTO_DIR" rev-parse --git-dir >/dev/null 2>&1 \
    || die "$PROTO_DIR is not a RaindropProto checkout; clone it next to this repository, or pass --proto-dir"
[ -f "$PROTO_DIR/raindrop/v1/server.proto" ] || die "$PROTO_DIR does not look like RaindropProto (no raindrop/v1/server.proto)"
PROTO_SHA="$(git -C "$PROTO_DIR" rev-parse --verify --quiet "$PROTO_REF^{commit}")" \
    || die "'$PROTO_REF' is not a commit, tag or branch of $PROTO_DIR"
PROTO_SHORT="$(git -C "$PROTO_DIR" rev-parse --short=7 "$PROTO_SHA")"
if [ "$PROTO_SHA" = "$(git -C "$PROTO_DIR" rev-parse HEAD)" ] && [ -n "$(git -C "$PROTO_DIR" status --porcelain)" ]; then
    warn "RaindropProto has uncommitted changes; they are NOT part of this deploy, only commit $PROTO_SHORT is"
fi

# ---- Talking to the server ---------------------------------------------------------------------------

TARGET="$SSH_USER@$HOST"
# One connection, opened once and reused, so a password or key passphrase is asked for once per run
# rather than once per command. The socket lives under /tmp because unix socket paths are short
# (about 104 characters) and $TMPDIR on a Mac is not. Host keys are checked as usual: nothing here
# turns that off.
CTL_DIR="$(mktemp -d /tmp/raindrop-deploy.XXXXXX)"
SSH_OPTS=(-o ControlMaster=auto -o "ControlPath=$CTL_DIR/%C" -o ControlPersist=300
          -o ConnectTimeout=10 -o ServerAliveInterval=15 -o ServerAliveCountMax=8 -o LogLevel=ERROR)

cleanup() {
    ssh "${SSH_OPTS[@]}" -O exit "$TARGET" >/dev/null 2>&1 || true
    rm -rf "$CTL_DIR"
}
trap cleanup EXIT

# The address to use in URLs, which differs from $HOST when that is a name from ~/.ssh/config.
HOST_ADDR="$(ssh -G "$TARGET" 2>/dev/null | sed -n 's/^hostname //p' | head -n 1)"
HOST_ADDR="${HOST_ADDR:-$HOST}"

# Wraps a value in single quotes so it survives the remote shell.
sq() { printf "'%s'" "$(printf '%s' "$1" | sed "s/'/'\\\\''/g")"; }

# The part that runs on the server. It is sent over stdin and run from a temporary file rather than
# with `bash -s`, because a command inside it that reads stdin would otherwise swallow the rest of it.
# Progress goes to stderr, which reaches the terminal live; answers this script needs go to stdout.
read -r -d '' REMOTE_SCRIPT <<'REMOTE' || true
set -euo pipefail

mode="$1"
app="$2"
shift 2

say()  { printf '  %s\n' "$*" >&2; }
warn() { printf '  ! %s\n' "$*" >&2; }
die()  { printf '  ✗ %s\n' "$*" >&2; exit 1; }

case "$app" in
    /*) ;;
    *)  app="$HOME/$app" ;;
esac
app="${app%/}"
if [ -z "$app" ] || [ "$app" = "$HOME" ]; then
    die "'$app' cannot be the deploy directory; give it a directory of its own"
fi

need_docker() {
    command -v docker >/dev/null 2>&1 || die "docker is not installed for $(id -un) on this machine"
    local err
    err="$(docker info 2>&1 >/dev/null)" || die "docker is not usable as $(id -un): $(printf '%s' "$err" | head -n 1)
      If that is a permission error: sudo usermod -aG docker $(id -un), then log out and back in."
    docker compose version >/dev/null 2>&1 \
        || die "the Docker Compose v2 plugin is missing ('docker compose version' fails). Debian/Ubuntu: apt install docker-compose-plugin"
}

# A value from the server's .env, for the few things read outside of compose (the port).
env_value() {
    [ -f "$app/.env" ] || return 0
    tr -d '\r' < "$app/.env" \
        | sed -n "s/^[[:space:]]*$1[[:space:]]*=[[:space:]]*//p" | tail -n 1 \
        | sed -e 's/[[:space:]]*#.*$//' -e 's/^"\(.*\)"$/\1/' -e "s/^'\(.*\)'\$/\1/"
}

rand_hex() { head -c "$1" /dev/urandom | od -An -tx1 | tr -d ' \n'; }
rand_b64() { head -c "$1" /dev/urandom | base64 | tr -d '\n'; }

write_env() {
    local node_ip="$1"
    ( umask 077
      cat > .env.new <<EOF
# Created by deploy.sh on $(date -u +%Y-%m-%dT%H:%M:%SZ). Lives on this server only: deploy.sh never
# overwrites it and never copies it anywhere. Back it up together with data/ - data/postgres was
# initialised with the password below, and losing it means losing access to that database.

POSTGRES_USER=raindrop
POSTGRES_PASSWORD=$(rand_hex 24)
POSTGRES_DB=raindrop
# Bound to loopback: Raindrop reaches Postgres over the compose network, so it needs no exposure to
# the LAN. Use plain 5432 to publish it.
POSTGRES_PORT=127.0.0.1:5432

# docker-compose.yml requires this even though the sqlserver service is not started here.
MSSQL_SA_PASSWORD=Rd-$(rand_hex 16)
MSSQL_PORT=1433

# The address LiveKit tells clients to send audio to: this server's LAN address.
LIVEKIT_NODE_IP=$node_ip
LIVEKIT_API_KEY=raindrop
LIVEKIT_API_SECRET=$(rand_b64 48)

RAINDROP_PORT=5001

# DEVELOPMENT ONLY. deploy.sh refuses to deploy while this is true.
RAINDROP_DEV_WIPE_DATABASE=false
EOF
    )
    mv .env.new .env
}

write_override() {
    cat > docker-compose.override.yml <<'YAML'
# Written by deploy.sh on every deploy; edits here are overwritten. Builds the server image from the
# source exported into ./src instead of from the directory holding this file (which also holds data/),
# and the wire protocol from the RaindropProto exported into ./proto.
services:
  server:
    build:
      context: ./src
      additional_contexts:
        raindrop_proto: ./proto
      labels:
        raindrop.deploy: "true"   # lets deploy.sh clean up superseded images without touching others
YAML
}

mode_check() {
    need_docker
    local probe="$app" free_kb
    while [ ! -d "$probe" ]; do probe="$(dirname "$probe")"; done
    free_kb="$(df -Pk "$probe" | awk 'NR==2 {print $4}')"
    say "docker $(docker version --format '{{.Server.Version}}' 2>/dev/null || echo '?'), compose $(docker compose version --short 2>/dev/null || echo '?'), $(uname -m), $((free_kb / 1048576)) GB free"
    if [ "$free_kb" -lt 3145728 ]; then
        warn "less than 3 GB free: the first build pulls about 1 GB of base images"
    fi

    local port cid running=no
    port="$(env_value RAINDROP_PORT)"
    if [ -f "$app/docker-compose.yml" ]; then
        cid="$(cd "$app" && docker compose ps -q server 2>/dev/null | head -n 1)" || cid=""
        if [ -n "$cid" ] && [ "$(docker inspect -f '{{.State.Running}}' "$cid" 2>/dev/null)" = true ]; then running=yes; fi
    fi
    printf 'env=%s\n' "$([ -f "$app/.env" ] && echo present || echo missing)"
    printf 'port=%s\n' "${port:-5001}"
    printf 'running=%s\n' "$running"
    printf 'deployed=%s\n' "$(sed -n 's/^sha=//p' "$app/.deployed" 2>/dev/null | head -n 1)"
}

mode_deploy() {
    local sha="$1" node_ip="$2" proto_sha="$3"
    need_docker
    case "$sha" in ""|*[!0-9a-f]*) die "'$sha' is not a commit id" ;; esac
    case "$proto_sha" in ""|*[!0-9a-f]*) die "'$proto_sha' is not a commit id" ;; esac

    mkdir -p "$app"
    cd "$app"

    # One deploy at a time. flock lets go when the process ends, so a killed deploy leaves no stale lock.
    if command -v flock >/dev/null 2>&1; then
        exec 9> .deploy.lock
        flock -n 9 || die "another deploy is already running in $app"
    fi
    trap 'rm -rf "$app/.incoming" "$app/src.new" "$app/proto.new" "$app/.env.new"' EXIT

    local tarball=".incoming/$sha.tar.gz"
    [ -s "$tarball" ] || die "the uploaded source ($tarball) is missing"

    say "Unpacking the source"
    rm -rf src.new
    mkdir src.new
    tar -xzf "$tarball" -C src.new
    [ -f src.new/docker-compose.yml ] && [ -f src.new/ServerCli/Dockerfile ] \
        || die "the uploaded source is not a RaindropServer checkout"

    local proto_tarball=".incoming/proto-$proto_sha.tar.gz"
    [ -s "$proto_tarball" ] || die "the uploaded wire protocol ($proto_tarball) is missing"
    rm -rf proto.new
    mkdir proto.new
    tar -xzf "$proto_tarball" -C proto.new
    [ -f proto.new/raindrop/v1/server.proto ] || die "the uploaded wire protocol is not a RaindropProto checkout"

    if [ ! -f .env ]; then
        if [ -e data/postgres ]; then
            die ".env is missing but data/postgres exists. Its database password came from the .env that is gone, and a newly generated one would lock the server out of its own data. Restore .env from a backup, or move data/ aside to start from scratch."
        fi
        say "No .env yet: generating one with fresh passwords (mode 600)"
        write_env "$node_ip"
    fi

    # Judge the new compose file against this server's .env before any live file is replaced.
    say "Checking the configuration"
    local cfg
    cfg="$(docker compose -f src.new/docker-compose.yml --project-directory "$app" config 2>&1)" || {
        printf '%s\n' "$cfg" | sed 's/^/    /' >&2
        die "docker compose rejects this configuration (above). Fix $app/.env on the server, or the compose file, and deploy again."
    }
    cfg_value() {
        printf '%s\n' "$cfg" | sed -n "s/^[[:space:]]*$1:[[:space:]]*//p" | head -n 1 \
            | sed -e 's/^"\(.*\)"$/\1/' -e "s/^'\(.*\)'\$/\1/"
    }
    local problems="" value
    value="$(cfg_value RAINDROP_DEV_WIPE_DATABASE | tr 'A-Z' 'a-z')"
    case "$value" in
        true|yes|on|y|1) problems="$problems
    - RAINDROP_DEV_WIPE_DATABASE is on: the server would drop every table each time it starts. Remove it from $app/.env." ;;
    esac
    value="$(cfg_value POSTGRES_PASSWORD | tr 'A-Z' 'a-z')"
    if [ "$value" = "change-me" ]; then
        problems="$problems
    - POSTGRES_PASSWORD is still the .env.example placeholder. Set a real one in $app/.env (before the database is first created)."
    fi
    if [ -f docker-compose.override.yml ] && ! grep -q '^# Written by deploy.sh' docker-compose.override.yml; then
        problems="$problems
    - $app/docker-compose.override.yml exists and was not written by deploy.sh, which needs that file name to point the build at src/. Move it aside."
    fi
    if [ -n "$problems" ]; then
        printf '  ✗ Not deploying:%s\n' "$problems" >&2
        exit 1
    fi

    say "Installing the new source"
    rm -rf src.old proto.old
    [ -d src ] && mv src src.old
    [ -d proto ] && mv proto proto.old
    mv src.new src
    mv proto.new proto
    rm -rf src.old proto.old
    cp src/docker-compose.yml src/livekit.yaml .
    write_override

    say "Building the image (the running server keeps serving until this succeeds)"
    docker compose build server

    # The image runs as a non-root user (APP_UID, as its Dockerfile says), and Docker creates a missing
    # bind-mount directory owned by root, so on Linux the server could not write its certificate, key
    # or attachments. Docker Desktop hides this. Fix the owner from inside a throwaway container, which
    # needs no sudo; only entries with the wrong owner are touched, so it is instant once right.
    say "Making data/server writable for the container's user"
    docker compose run --rm -T --no-deps --user 0 --entrypoint sh server -c \
        'find /app/data \( ! -uid "$APP_UID" -o ! -gid "$APP_UID" \) -exec chown "$APP_UID:$APP_UID" {} +' >&2 \
        || warn "could not change the owner of data/server; if the server then cannot write its certificate and key, chown data/server to the container's user by hand"

    say "Starting the stack"
    docker compose up -d server

    docker image prune -f --filter 'label=raindrop.deploy=true' >/dev/null 2>&1 || true
}

# Written only once the new server has answered, so .deployed always names the last release known to
# work, which is what a rollback wants.
mode_record() {
    local sha="$1" ref="$2" proto_sha="$3" now
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    cd "$app"
    printf 'sha=%s\nref=%s\nproto=%s\ntime=%s\n' "$sha" "$ref" "$proto_sha" "$now" > .deployed
    printf '%s %s (%s) proto %s\n' "$now" "$sha" "$ref" "$proto_sha" >> deploy.log
}

# Asks the running server for its handshake from inside its own container, as key=value lines. Prints
# nothing while it is not answering.
mode_probe() {
    local cid=""
    [ -f "$app/docker-compose.yml" ] || return 0
    cd "$app"
    cid="$(docker compose ps -q server 2>/dev/null | head -n 1)" || cid=""
    [ -n "$cid" ] || return 0
    # Only an image that sets SERVERCLI_HAS_PROBE has the probe command: an older ServerCli would ignore
    # the argument and try to start a second server inside the container.
    docker compose exec -T server sh -c \
        '[ -n "${SERVERCLI_HAS_PROBE:-}" ] && exec dotnet ServerCli.dll probe https://127.0.0.1:5001' 2>/dev/null || true
}

mode_status() {
    local cid=""
    if [ -f "$app/docker-compose.yml" ]; then
        cid="$(cd "$app" && docker compose ps -q server 2>/dev/null | head -n 1)" || cid=""
    fi
    if [ -z "$cid" ]; then
        echo "state=missing restarts=0"
    else
        docker inspect -f 'state={{.State.Status}} restarts={{.RestartCount}}' "$cid"
    fi
}

mode_logs() {
    cd "$app"
    docker compose ps >&2 || true
    docker compose logs --no-color --tail 80 server >&2 || true
}

case "$mode" in
    check)  mode_check ;;
    deploy) mode_deploy "$@" ;;
    record) mode_record "$@" ;;
    status) mode_status ;;
    logs)   mode_logs ;;
    probe)  mode_probe ;;
    *)      die "unknown mode '$mode'" ;;
esac
REMOTE

# run_remote <mode> [args...]: runs REMOTE_SCRIPT on the server; the deploy directory is always the
# first argument after the mode.
run_remote() {
    local mode="$1" args="" a
    shift
    for a in "$mode" "$REMOTE_DIR" "$@"; do args="$args $(sq "$a")"; done
    ssh "${SSH_OPTS[@]}" "$TARGET" \
        'f=$(mktemp) && cat > "$f" && bash "$f"'"$args"'; rc=$?; rm -f "$f"; exit $rc' \
        <<< "$REMOTE_SCRIPT"
}

probe_field() { sed -n "s/^$1=//p" | head -n 1; }
server_info() { run_remote probe 2>/dev/null || true; }

# ---- Look at the server ------------------------------------------------------------------------------

log "Connecting to $TARGET"
if ! ssh -n "${SSH_OPTS[@]}" "$TARGET" true; then
    fail "could not open an SSH connection to $TARGET"
    echo "  - First time from this machine? Connect once by hand so ssh records the host key: ssh $TARGET" >&2
    echo "  - Asked for a password on every run? Install a key: ssh-keygen -t ed25519, then ssh-copy-id $TARGET" >&2
    exit 1
fi

log "Checking the server"
STATE="$(run_remote check)" || exit 1
state() { printf '%s\n' "$STATE" | sed -n "s/^$1=//p" | head -n 1; }
PORT="$(state port)"
[[ "$PORT" =~ ^[0-9]{1,5}$ ]] || die "the server's .env has RAINDROP_PORT='$PORT', which is not a port number"
LAST_GOOD="$(state deployed)"

BEFORE_INFO=""
if [ "$(state running)" = yes ]; then BEFORE_INFO="$(server_info)"; fi
BEFORE_KEY="$(printf '%s\n' "$BEFORE_INFO" | probe_field identityKey)"
BEFORE_FINGERPRINT="$(printf '%s\n' "$BEFORE_INFO" | probe_field tlsFingerprint)"
if [ "$(state running)" = yes ] && [ -z "$BEFORE_KEY" ]; then
    warn "the running server did not say its identity key (it may be a release from before the probe command), so this deploy cannot check that the new one comes back with the same key. Compare the 'Server identity' line in its log by hand afterwards."
fi

NODE_IP="127.0.0.1"
if [[ "$HOST_ADDR" =~ ^[0-9]{1,3}(\.[0-9]{1,3}){3}$ ]]; then NODE_IP="$HOST_ADDR"; fi

# ---- The plan ----------------------------------------------------------------------------------------

log "Plan"
# The checked-out branch says something about HEAD only; for a tag or another commit it would mislead.
WHAT="$REF"
if [ "$REF" = HEAD ] && [ -n "$BRANCH" ]; then WHAT="HEAD, branch $BRANCH"; fi
printf '  %-10s %s  "%s"  (%s)\n' "deploy" "$SHORT" "$SUBJECT" "$WHAT"
printf '  %-10s RaindropProto %s  "%s"\n' "protocol" "$PROTO_SHORT" "$(git -C "$PROTO_DIR" log -1 --format=%s "$PROTO_SHA")"
printf '  %-10s %s:%s\n' "to" "$TARGET" "$REMOTE_DIR"
if [ -n "$LAST_GOOD" ]; then
    printf '  %-10s %s (the last deploy that came up healthy)\n' "replaces" "${LAST_GOOD:0:7}"
else
    printf '  %-10s nothing recorded yet: a first deploy, or one made by hand\n' "replaces"
fi
if [ "$(state env)" = present ]; then
    printf '  %-10s kept as it is\n' ".env"
else
    printf '  %-10s missing: one will be created with generated passwords (mode 600, on the server only)\n' ".env"
    if [ "$NODE_IP" = "127.0.0.1" ]; then
        warn "$HOST_ADDR is not an IPv4 address, so LIVEKIT_NODE_IP will be 127.0.0.1: voice would only work on the server itself. Edit it in the server's .env afterwards."
    else
        printf '  %-10s LiveKit will advertise %s for audio\n' "" "$NODE_IP"
    fi
fi
printf '  %-10s never touched\n' "data/"

if $DRY_RUN; then
    log "Dry run: nothing was changed on the server"
    exit 0
fi

if ! $ASSUME_YES; then
    [ -t 0 ] || die "no terminal to ask on; pass --yes to deploy without confirmation"
    echo
    read -r -p "Deploy? [y/N] " confirm
    case "$confirm" in
        y|Y|yes|YES) ;;
        *) echo "Aborted; nothing was changed."; exit 1 ;;
    esac
fi

# ---- Deploy ------------------------------------------------------------------------------------------

STARTED=$SECONDS   # after the prompt, so the time reported below is the deploy's and not how long you thought
log "Uploading $SHORT, and RaindropProto $PROTO_SHORT"
# The directory is resolved against $HOME exactly as REMOTE_SCRIPT does, so both name the same place
# whatever directory the SSH session happens to start in.
git -C "$ROOT_DIR" archive --format=tar.gz "$SHA" \
    | ssh "${SSH_OPTS[@]}" "$TARGET" \
        "d=$(sq "$REMOTE_DIR"); case \$d in /*) ;; *) d=\$HOME/\$d ;; esac; mkdir -p \"\$d/.incoming\" && cat > \"\$d/.incoming/$SHA.tar.gz\""
git -C "$PROTO_DIR" archive --format=tar.gz "$PROTO_SHA" \
    | ssh "${SSH_OPTS[@]}" "$TARGET" \
        "d=$(sq "$REMOTE_DIR"); case \$d in /*) ;; *) d=\$HOME/\$d ;; esac; mkdir -p \"\$d/.incoming\" && cat > \"\$d/.incoming/proto-$PROTO_SHA.tar.gz\""

log "Building and restarting on $HOST_ADDR"
if ! run_remote deploy "$SHA" "$NODE_IP" "$PROTO_SHA"; then
    fail "the deploy stopped on the server (see above). The running server was left alone unless the output reached 'Starting the stack'."
    exit 1
fi

log "Waiting for the server to answer (up to ${HEALTH_TIMEOUT}s)"
HEALTHY=false
DEADLINE=$((SECONDS + HEALTH_TIMEOUT))
GRACE_ENDS=$((SECONDS + CRASH_LOOP_GRACE))
while [ "$SECONDS" -lt "$DEADLINE" ]; do
    AFTER_INFO="$(server_info)"
    if [[ "$AFTER_INFO" == *identityKey=?* ]]; then HEALTHY=true; break; fi

    STATUS="$(run_remote status 2>/dev/null || true)"
    RESTARTS="$(printf '%s\n' "$STATUS" | sed -n 's/.*restarts=\([0-9]*\).*/\1/p' | head -n 1)"
    case "$STATUS" in
        *state=exited*|*state=dead*|*state=missing*) break ;;
    esac
    # `unless-stopped` restarts a server that refuses to start, so a crash shows as a restart count. The
    # server exits when it cannot reach its database and leaves the retrying to Docker, so a first start
    # can restart a few times and then be fine; only a server still restarting after a grace period is
    # called a crash loop.
    if [ "${RESTARTS:-0}" -ge 3 ] && [ "$SECONDS" -ge "$GRACE_ENDS" ]; then break; fi
    sleep 3
done

if ! $HEALTHY; then
    fail "the new server did not come up. The last lines of its log:"
    run_remote logs || true
    echo >&2
    if [ "$LAST_GOOD" = "$SHA" ]; then
        echo "  This is the commit that last came up healthy here, so the code did not change: look at the log above and at the server's .env." >&2
    elif [ -n "$LAST_GOOD" ]; then
        echo "  If the log points at this release rather than at the server's .env, go back to the last one that worked: ./deploy.sh --ref ${LAST_GOOD:0:7}" >&2
    fi
    exit 1
fi

AFTER_KEY="$(printf '%s\n' "$AFTER_INFO" | probe_field identityKey)"
AFTER_FINGERPRINT="$(printf '%s\n' "$AFTER_INFO" | probe_field tlsFingerprint)"

# Checked before anything is called a success or recorded: a server that came back as a different
# server has not been deployed, whatever it answers, and must not become the "last good" release.
if [ -n "$BEFORE_KEY" ] && [ "$BEFORE_KEY" != "$AFTER_KEY" ]; then
    fail "THE SERVER CAME BACK WITH A DIFFERENT IDENTITY KEY. Clients that pinned this server will refuse to connect."
    echo "  data/server on the server was empty or not mounted when the container started." >&2
    echo "  Restore data/server from a backup (server-identity.key and raindrop.pfx) and deploy again; do not let clients pin the new key." >&2
    echo "  This release was not recorded as deployed." >&2
    exit 1
fi

run_remote record "$SHA" "$REF" "$PROTO_SHA"
ok "Deployed $SHORT to $TARGET in $((SECONDS - STARTED))s"

echo
printf '  %-13s https://%s:%s\n' "Server" "$HOST_ADDR" "$PORT"
printf '  %-13s %s\n' "Fingerprint" "$AFTER_FINGERPRINT"
printf '  %-13s ssh %s '"'"'cd %s && docker compose logs -f server'"'"'\n' "Logs" "$TARGET" "$REMOTE_DIR"
if [ "$(state env)" != present ]; then
    printf '  %-13s %s:%s/.env — back it up together with data/\n' "Passwords" "$TARGET" "$REMOTE_DIR"
fi
echo

if [ -z "$BEFORE_KEY" ]; then
    echo "  The server was not answering before this deploy, so there was no identity key to compare with."
    echo "  A client that connects now will pin the fingerprint above."
else
    ok "The identity key is unchanged: clients that pinned this server still trust it."
    if [ "$BEFORE_FINGERPRINT" != "$AFTER_FINGERPRINT" ]; then
        warn "The TLS certificate was replaced (its fingerprint changed). Clients follow that only because the identity key vouches for the new one."
    fi
fi
