# Configuration

`ServerCli/raindrop.json` is copied next to the executable and read at startup by `ServerConfig.Load()`
([`ServerCli/ServerConfig.cs`](../ServerCli/ServerConfig.cs)). Environment variables override it — this
is how the Docker container is configured without editing the file.

## `raindrop.json` reference

```json
{
  "urls": [ "https://localhost:5001" ],

  "tls": {
    "enabled": true,
    "certificatePath": "raindrop.pfx",
    "certificatePassword": null,
    "hostNames": null
  },

  "database": {
    "provider": "sqlite",
    "connectionString": "Data Source=raindrop.db",
    "autoCreateSchema": true
  },

  "users": {
    "dataRetentionDays": 730,
    "sessionIdleTimeoutHours": 12,
    "minimumPasswordLength": 8
  },

  "channels": {
    "maximumMessageLength": 4000,
    "defaultHistoryPageSize": 50,
    "maximumHistoryPageSize": 200
  },

  "roles": {
    "defaultMemberPermissions": [ "view_channel", "send_messages", "create_channels", "connect", "speak" ],
    "channelCreatorModeratesOwnChannels": true,
    "firstMemberBecomesOwner": true
  },

  "voice": {
    "provider": null,
    "maximumParticipants": 25,
    "livekit": {
      "apiUrl": "http://localhost:7880",
      "apiKey": "devkey",
      "apiSecret": "secret"
    }
  }
}
```

### `urls`

Addresses to listen on. Relative TLS certificate paths (below) resolve against the executable's
directory; the SQLite database path resolves against the working directory.

### `tls`

See [Transport security](transport-security.md) for the full model (self-signed certificates,
fingerprint pinning).

- `enabled` — set to `false` only when a reverse proxy terminates TLS in front of the server, and use
  an `http://` URL in that case. An `https://` URL with TLS disabled is a startup error rather than a
  silent downgrade to plaintext.
- `certificatePath` — a self-signed certificate is generated here on first start if nothing exists yet
  (see [`ServerCertificate`](../RaindropServer/Security/ServerCertificate.cs)); point this at a
  CA-issued PFX instead to skip client-side pinning entirely.
- `certificatePassword` — the PFX's password, if any.
- `hostNames` — optional; used when generating a self-signed certificate.

**The certificate file must persist across restarts/redeploys.** Regenerating it changes the
fingerprint and locks out every client that pinned the old one.

### `database`

See [Database modules](database-modules.md) for the driver architecture.

- `provider` — the driver name to enable: `sqlite`, `postgres`, `sqlserver`, or a third-party driver's
  name. Case-insensitive.
- `connectionString` — see the per-provider examples in [Database modules](database-modules.md#the-three-shipped-drivers).
- `autoCreateSchema` — when `true`, missing tables/indexes are created on every startup (idempotent);
  set `false` to manage schema separately.

### `users`

See [Users](users.md) for the domain behavior these configure.

- `dataRetentionDays` — how long a `left_preserved` account's username/password survive before
  `RetentionCleanupService` erases them; capped by `UserOptions.MaximumDataRetention` (two years).
- `sessionIdleTimeoutHours` — how long an idle session stays valid; also what presence
  (see [Members](members.md)) is sharpened or dulled by.
- `minimumPasswordLength` — enforced at registration and password change.

### `channels`

See [Messages](messages.md) for history paging behavior.

- `maximumMessageLength` — messages longer than this are refused with `message_too_long`.
- `defaultHistoryPageSize` — used when a history request omits `Limit`.
- `maximumHistoryPageSize` — a requested `Limit` above this is silently trimmed down to it.

### `roles`

See [Roles & permissions](roles-and-permissions.md) for the model these configure.

- `defaultMemberPermissions` — permission *names* (see the table in
  [Roles & permissions](roles-and-permissions.md#permissions)) seeding the `everyone` role **the first
  time this server ever runs, and never again after that** — changing this later doesn't retroactively
  change a server that's already been seeded. A permission name this build doesn't recognize is a
  startup error naming the offending setting, rather than a role that silently grants less than the
  owner intended.
- `channelCreatorModeratesOwnChannels` — whether creating a channel also grants moderation rights over
  it, independent of roles.
- `firstMemberBecomesOwner` — whether the first UUID to register is automatically granted the `owner`
  role (see [Roles & permissions](roles-and-permissions.md#the-two-built-in-roles)). Turn this off for
  a server whose owner should instead be granted the role explicitly from the console.

### `voice`

See [Voice](voice.md) for the model these configure.

- `provider` — the media server carrying voice calls: `livekit`, or `null` (the default) for none, in
  which case voice channels can be created but joining one is refused with `voice_unavailable`. Any
  other name is a startup error.
- `maximumParticipants` — the most members one call holds (`channel_full` beyond it).
- `livekit.apiUrl` — where *this server* reaches LiveKit's API and signalling; keep it private. Clients
  never connect to it: they go through this server's relay (`/api/channels/voice/signal`), over its
  pinned TLS. Only LiveKit's media ports (UDP 7882, TCP 7881) need to be reachable from outside.
- `livekit.apiKey` / `livekit.apiSecret` — the key pair LiveKit was configured with (`devkey`/`secret`
  in its `--dev` mode). Both are required when the provider is `livekit`; prefer
  `RAINDROP_LIVEKIT_API_SECRET` to writing the secret into this file.

## Environment variable overrides

Used by the Docker container instead of editing `raindrop.json` directly:

| Variable | Overrides |
|---|---|
| `RAINDROP_URLS` | `urls` |
| `RAINDROP_DB_PROVIDER` | `database.provider` |
| `RAINDROP_DB_CONNECTION_STRING` | `database.connectionString` |
| `RAINDROP_TLS_ENABLED` | `tls.enabled` |
| `RAINDROP_TLS_CERTIFICATE_PATH` | `tls.certificatePath` |
| `RAINDROP_TLS_CERTIFICATE_PASSWORD` | `tls.certificatePassword` |
| `RAINDROP_VOICE_PROVIDER` | `voice.provider` |
| `RAINDROP_LIVEKIT_API_URL` | `voice.livekit.apiUrl` |
| `RAINDROP_LIVEKIT_API_KEY` | `voice.livekit.apiKey` |
| `RAINDROP_LIVEKIT_API_SECRET` | `voice.livekit.apiSecret` |

## The owner console

Once the server is running with a TTY attached, [`ServerCli/Program.cs`](../ServerCli/Program.cs) runs
an interactive console on the main thread:

| Command | Does |
|---|---|
| `drivers` | Lists installed database modules and which one is enabled |
| `db` | Shows the active database provider and retention window |
| `fingerprint` | Prints the TLS fingerprint for clients to pin |
| `roles` | Lists this server's roles and what each grants |
| `grant <user> <role>` | Gives a member a role |
| `revoke <user> <role>` | Takes a role away from a member |
| `events` | Shows how many clients have the [event stream](events.md) open |
| `voice` | Lists who is in which [voice call](voice.md), and whether they are connecting, muted or deafened |
| `quit` | Shuts the server down gracefully |

`grant`/`revoke` go through the **running server's own `RoleService`** rather than writing to the
database directly — that's what keeps the in-memory permission cache
(see [Roles & permissions](roles-and-permissions.md#resolving-permissions)) correct immediately,
instead of only after a restart. They also bypass every permission check by design: whoever is sitting
at the console owns the machine, the database and the process, so there's nothing left for a
permission check to protect. This is the recovery path for a server whose owner has left, and the only
way to grant the very first role on a server with `firstMemberBecomesOwner` turned off.

Console `grant`/`revoke` are announced to connected clients as `member.roles` events, exactly as the
API's assign and unassign are — they go through the same `RoleService`.

With **no TTY attached** (e.g. a container started without one), the server skips the console entirely
and instead waits for `SIGINT`/`SIGTERM` to shut down. Either way, shutting down closes every open
event stream with `1001 server_stopping` first, so clients reconnect rather than guess.

## Related

- [Local development & Docker](local-development-and-docker.md) — running Postgres/SQL Server locally,
  building and running the Docker image.
