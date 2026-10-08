# Configuration

`ServerCli/raindrop.json` is copied next to the executable and read at startup by `ServerConfig.Load()`
([`ServerCli/ServerConfig.cs`](../ServerCli/ServerConfig.cs)). Environment variables override it — this
is how the Docker container is configured without editing the file.

## `raindrop.json` reference

```json
{
  "urls": [ "https://localhost:5001" ],

  "tls": {
    "certificatePath": "raindrop.pfx",
    "certificatePassword": null,
    "hostNames": null,
    "identityKeyPath": "server-identity.key",
    "allowTls12": false
  },

  "database": {
    "provider": "sqlite",
    "connectionString": "Data Source=raindrop.db",
    "autoCreateSchema": true,
    "wipeOnStartup": false
  },

  "users": {
    "dataRetentionDays": 730,
    "sessionIdleTimeoutHours": 12,
    "minimumPasswordLength": 8
  },

  "channels": {
    "maximumMessageLength": 4000,
    "defaultHistoryPageSize": 50,
    "maximumHistoryPageSize": 200,
    "allowPlaintextMessages": false
  },

  "roles": {
    "defaultMemberPermissions": [ "view_channel", "send_messages", "attach_files", "create_channels", "connect", "speak" ],
    "channelCreatorModeratesOwnChannels": true,
    "firstMemberBecomesOwner": true
  },

  "voice": {
    "provider": null,
    "maximumParticipants": 25,
    "startupCheck": "warn",
    "livekit": {
      "apiUrl": "http://localhost:7880",
      "apiKey": null,
      "apiSecret": null,
      "allowWeakSecret": false
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

- `enabled` — must be `true` (the default; leave it out). It is still read so an older `raindrop.json`
  loads, and `false` is a startup error: clients pin this server's own certificate and speak gRPC over
  HTTP/2 to it, so TLS always ends here. Every address in `urls` must be `https://` too. A reverse proxy
  in front of the server has to pass TLS through rather than terminate it.
- `certificatePath` — a self-signed certificate is generated here on first start if nothing exists yet
  (see [`ServerCertificate`](../RaindropServer/Security/ServerCertificate.cs)); point this at a
  CA-issued PFX instead to skip client-side pinning entirely.
- `certificatePassword` — the PFX's password, if any.
- `hostNames` — optional; used when generating a self-signed certificate.
- `identityKeyPath` — the server's long-lived identity key, generated on first start. It signs the
  certificate's fingerprint, so you can replace the certificate without clients refusing it. See
  [Transport security](transport-security.md#the-server-identity-key-changing-the-certificate-without-breaking-pins).
  Relative paths resolve against the executable, which in a container is inside the container's own
  layer and is lost when the container is recreated — so the Docker image sets
  `RAINDROP_TLS_IDENTITY_KEY_PATH` to put it on the volume beside the certificate.
- `allowTls12` — also accept TLS 1.2. Off by default (TLS 1.3 only, except on macOS; see Transport
  security).

**The identity key must persist across restarts and redeploys, and belongs in backups.** Losing it
locks out every client that pinned it. The certificate can be replaced freely as long as the identity
key stays.

### `database`

See [Database modules](database-modules.md) for the driver architecture.

- `provider` — the driver name to enable: `sqlite`, `postgres`, `sqlserver`, or a third-party driver's
  name. Case-insensitive.
- `connectionString` — see the per-provider examples in [Database modules](database-modules.md#the-three-shipped-drivers).
- `autoCreateSchema` — when `true`, missing tables/indexes are created on every startup (idempotent);
  set `false` to manage schema separately.
- `wipeOnStartup` — **development only.** When `true`, every table is dropped before the schema is
  (re)created, so the server starts from an empty database every time. No confirmation, no undo;
  said out loud as a startup warning whenever it's on. `RAINDROP_DEV_WIPE_DATABASE` overrides it —
  see [Local development & Docker](local-development-and-docker.md#wiping-the-database-on-every-start).

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
- `allowPlaintextMessages` — whether the plaintext `send` still works. Off by default: every message
  must be end-to-end encrypted (see [Messages](messages.md#end-to-end-encryption)). Turn it on only for
  clients that cannot encrypt.

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
- `startupCheck` — how hard the server looks for the media server before it starts listening: `off`
  not at all, `warn` (the default) to report an unreachable one and start anyway, `require` to refuse
  to start without it. A media server that answers and *refuses* these credentials stops the server
  whichever this is — see [Voice](voice.md#what-is-checked-at-startup).
- `livekit.apiUrl` — where *this server* reaches LiveKit's API and signalling; keep it private. Clients
  never connect to it: they go through this server's relay (`/api/channels/voice/signal`), over its
  pinned TLS. Only LiveKit's media ports (UDP 7882, TCP 7881) need to be reachable from outside.
- `livekit.apiUrl` must be `https://` unless it is a loopback address or a single-label (container)
  host name.
- `livekit.apiKey` / `livekit.apiSecret` — the key pair LiveKit was configured with (`LIVEKIT_API_KEY`
  / `LIVEKIT_API_SECRET` in `.env` for docker-compose). Both are required when the provider is
  `livekit`. The secret must be 32 or more random characters (`openssl rand -base64 48`). Prefer
  `RAINDROP_LIVEKIT_API_SECRET` to writing the secret into this file.
- `livekit.allowWeakSecret` — accept a short or well-known secret such as LiveKit's `--dev` `secret`.
  Only for a throwaway server on your own machine.

### `attachments`

See [Attachments](attachments.md) for the model these configure. Files are end-to-end encrypted, so
the server enforces these rules on what an upload declares in the clear — its extension and a
ciphertext length that fixes the plaintext length exactly — and never sees a name or contents.

```json
"attachments": {
  "enabled": true,
  "storagePath": "attachments",
  "maximumFileSizeMegabytes": 25,
  "maximumFileSizeMegabytesByExtension": { "mp4": 500, "mov": 500, "webm": 500, "zip": 100 },
  "allowedExtensions": null,
  "blockedExtensions": [ "exe", "msi", "bat", "cmd", "com", "scr", "pif", "vbs", "vbe", "jse", "wsf", "hta", "cpl", "ps1", "lnk", "jar", "apk" ],
  "allowFilesWithoutExtension": true,
  "maximumFilesPerMessage": 10,
  "pendingUploadExpiryHours": 24,
  "maximumPendingUploadsPerMember": 20,
  "preview": {
    "extensions": [ "png", "jpg", "jpeg", "gif", "webp", "mp4", "webm", "mov", "mp3", "m4a", "ogg", "wav", "flac", "opus" ],
    "autoLoadMaximumMegabytes": 10
  }
}
```

- `enabled` — whether members may add files at all. Off, the upload calls answer
  `attachments_disabled`, a message naming a file is refused, and `attachments.v1` is not advertised.
- `storagePath` — the folder encrypted files are kept in, relative to the executable
  (`/app/data/attachments` in Docker). **Back it up with the database.** Checked for writability at
  startup; a disk with less room than the largest accepted file is a warning.
- `maximumFileSizeMegabytes` — the largest file, in megabytes of plaintext (1 MB = 1,048,576 bytes),
  between 1 and 1,048,576; above 4,096 is a warning.
- `maximumFileSizeMegabytesByExtension` — larger or smaller limits for particular extensions. A limit
  for an extension that can never be uploaded is an error.
- `allowedExtensions` — `null` accepts any type that is not blocked; a list accepts only those.
- `blockedExtensions` — never accepted, whatever `allowedExtensions` says; left out, the list above
  applies, and `[]` blocks nothing. An extension in both lists is an error.
- `allowFilesWithoutExtension` — whether a file with no extension may be uploaded. An empty
  `allowedExtensions` with this off accepts nothing and is an error.
- `maximumFilesPerMessage` — 1 to 100 files per message (each may also carry a thumbnail).
- `pendingUploadExpiryHours` — uploads never sent in a message are thrown away after this (1 hour to
  30 days).
- `maximumPendingUploadsPerMember` — how many uploads one member may have waiting; fewer than
  `maximumFilesPerMessage` is an error.
- `preview.extensions` — the types clients show inline (images, video, audio) when they can render
  them; everything else is a file to save. An entry that can never be uploaded is a warning.
- `preview.autoLoadMaximumMegabytes` — the largest previewable file a client loads without being
  asked; `0` never loads one unasked.

Extensions are written lower case without the dot: `".png"` or `"PNG"` is an error that says how to
write it, and a repeated entry is a warning. A section of settings that includes a map (the size
overrides) is still checked strictly: a misspelled setting beside it is reported, and so is a map
value that is not a whole number.

## Environment variable overrides

Used by the Docker container instead of editing `raindrop.json` directly:

| Variable | Overrides |
|---|---|
| `RAINDROP_URLS` | `urls` |
| `RAINDROP_DB_PROVIDER` | `database.provider` |
| `RAINDROP_DB_CONNECTION_STRING` | `database.connectionString` |
| `RAINDROP_DEV_WIPE_DATABASE` | `database.wipeOnStartup` — **development only**, drops every table on startup |
| `RAINDROP_TLS_ENABLED` | `tls.enabled` — only `true` is accepted |
| `RAINDROP_TLS_CERTIFICATE_PATH` | `tls.certificatePath` |
| `RAINDROP_TLS_CERTIFICATE_PASSWORD` | `tls.certificatePassword` |
| `RAINDROP_TLS_IDENTITY_KEY_PATH` | `tls.identityKeyPath` — the Docker image and `docker-compose.yml` point it at `/app/data/server-identity.key`, on the mounted volume |
| `RAINDROP_VOICE_PROVIDER` | `voice.provider` |
| `RAINDROP_VOICE_STARTUP_CHECK` | `voice.startupCheck` |
| `RAINDROP_LIVEKIT_API_URL` | `voice.livekit.apiUrl` |
| `RAINDROP_LIVEKIT_API_KEY` | `voice.livekit.apiKey` |
| `RAINDROP_LIVEKIT_API_SECRET` | `voice.livekit.apiSecret` |
| `RAINDROP_LIVEKIT_ALLOW_WEAK_SECRET` | `voice.livekit.allowWeakSecret` |
| `RAINDROP_ATTACHMENTS_ENABLED` | `attachments.enabled` |
| `RAINDROP_ATTACHMENTS_PATH` | `attachments.storagePath` |
| `RAINDROP_ATTACHMENTS_MAX_MB` | `attachments.maximumFileSizeMegabytes` (a whole number) |

## Startup validation

Every setting in this file is checked before the server listens, and **everything wrong with it is
reported at once** — an owner with three mistakes should not have to restart three times to find them
all. Errors stop the server; warnings are printed and it carries on.

```
Checked raindrop.json:
  error   voice.maxParticipants: is not a setting this server has, so it does nothing.
          Did you mean 'maximumParticipants'?
  error   database.provider: is 'sqlite3', which is not an installed database driver.
          Did you mean 'sqlite'? Installed drivers: postgres, sqlite, sqlserver.
  warning channels.allowPlaintextMessages: is on, so members may post messages this server can read
          and store in the clear.
Cannot start Raindrop until the errors above are fixed.
```

### How it is put together

Nothing central knows what the settings mean. Each component validates its own
([`ConfigurationReport`](../RaindropServer/Configuration/ConfigurationReport.cs) collects what they
find, naming each problem by the setting path it lives at):

| Checked by | Covers |
|---|---|
| `ServerConfig.Load` | the file itself: JSON that does not parse, settings this build does not have, values of the wrong kind, `RAINDROP_*` variables that are empty, malformed or misspelled |
| `DatabaseOptions.Validate` | a driver that is installed, a connection string that parses |
| `UserOptions.Validate` | session and retention windows, password length bounds that no password could satisfy |
| `ChannelOptions.Validate` | message and history limits, and the pairs of them that contradict each other |
| `RoleOptions.Validate` | an `everyone` role seeded with permissions that administer the server |
| `VoiceOptions.Validate` | call size, join grace and reconcile interval |
| `LiveKitOptions.Validate` | the media server's address and credentials — see [Voice](voice.md#what-is-checked-at-startup) |
| `ServerHostOptions.Validate` | the listening addresses, and the certificate against the names they promise |

A **misspelled setting is an error, not a shrug**. JSON deserialization ignores a name it does not
know, which leaves a server running on a default nobody chose: `apisecrets` instead of `apiSecret`
switches off no feature and prints no warning, it just never reaches LiveKit. The same goes for a
`RAINDROP_*` variable this build does not read, which is a warning since the environment belongs to
more than this server.

### Going and looking

Some things can only be found out by asking. After the services are registered and before the port
opens, every registered
[`IStartupCheck`](../RaindropServer/Configuration/IStartupCheck.cs) runs
(`services.RunRaindropStartupChecksAsync()`), against a container built for the purpose and thrown
away afterwards. The checks this build ships:

- **the certificate** — without its private key (an error; nothing can serve TLS with it), or
  expired, not yet valid (a clock problem), or not covering one of the addresses the server listens
  on (warnings: clients pin the fingerprint and accept it, everything else validating it will not)
- **the media server** — `LiveKitStartupCheck`: the host resolves, something answers, it is LiveKit's
  API, it accepts this server's key and secret, and its clock agrees with this one's
- **the key files** — `raindrop.pfx` and `server-identity.key` readable, creatable where they are
  expected to be generated, and not left readable by every account on the machine

A plugin adds its own with `services.AddStartupCheck<T>()`; a plugin's options validate themselves by
taking a `ConfigurationReport` the same way the built-in ones do.

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
| `attachments` | Shows the [attachment](attachments.md) folder, how many files are stored and how much room they take, and uploads waiting to be sent |
| `reports` | Shows the most recently [reported messages](messages.md#reporting-an-encrypted-message) (the latest 20) |
| `purge-plaintext` | Deletes every message stored before end-to-end encryption, and the deleted-message archive, after a typed `yes` |
| `help` | Lists these commands |
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
