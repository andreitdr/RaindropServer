# Owner dashboard

A web page for the server's owner: whether the server is up and what it is using, the machine (or
container) it runs on, how many clients are connected and how many accounts it has — and a way to
**recover a member's account** without the console. It lives in
[`RaindropServer.Dashboard/`](../RaindropServer.Dashboard/), a module like the LiveKit one, so the
library keeps knowing no transport.

**It is not for clients and they never see it.** It has a port of its own (5002 by default), is never
advertised in `GetServerInfo` or the capabilities, and the client-facing port does not answer any of
its routes. Raindrop clients keep speaking only gRPC.

## Turning it on

`dashboard` in `raindrop.json` ([Configuration](configuration.md#dashboard)); the shipped file and the
Docker image turn it on. On start the server prints where it is and, until it has been set up, a
one-time setup code:

```
Dashboard: https://0.0.0.0:5002 (from 127.0.0.0/8, ::1/128, 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, fc00::/7, fe80::/10)
Dashboard setup code: 5EW3-HY65-RZGF  — open the dashboard and enter it to choose its username and password.
```

Open `https://<server address>:5002` in a browser. The certificate is the server's own self-signed
one, so the browser warns once; check its fingerprint against `fingerprint` in the console. In Docker
the code is in `docker compose logs server`.

## Who can reach it

Three layers, each enough on its own to stop a stranger:

1. **Its own port.** `ServerHost` listens on it as one more [`ServerListener`](../RaindropServer/ServerListener.cs)
   with the same certificate and TLS settings. Endpoints carry `ListenerScope("dashboard")`: they answer
   on that port only, and nothing else answers there — gRPC and the voice relay are 404 on 5002, the
   dashboard is 404 on 5001. Validation refuses a dashboard URL on a port clients already use.
2. **Allowed networks.** `dashboard.allowedNetworks` (CIDR blocks; loopback and the private ranges by
   default). A connection from anywhere else is closed by a Kestrel connection middleware **before the
   TLS handshake**, so it learns nothing, not even the certificate. An IPv4 address arriving as IPv6
   (`::ffff:…`) is judged as IPv4. An entry with bits set past its prefix (`192.168.1.5/24`) is an error
   rather than silently widened; `0.0.0.0/0` or `::/0` is a warning.
3. **Its own sign-in** — below.

**Docker caveat.** Docker Desktop (macOS, Windows) and Docker's loopback proxy hand the container
connections from a gateway address (`172.17.0.1`, `192.168.65.x`), which is private — so on those the
allowlist sees every client as local and the sign-in is what protects the page. Narrow it where it is
published instead: `RAINDROP_DASHBOARD_PUBLISH=192.168.1.10:5002` (a LAN address) or `127.0.0.1:5002`
(reach it through an SSH tunnel). On Linux with Docker's default iptables port mapping, the real client
address is kept and the allowlist works as on a bare machine.

## Signing in

The dashboard has **one account of its own** — a username and password, not a Raindrop account: most
owners' Raindrop accounts have no password since clients sign in with a device key, and the dashboard
has to keep working for an owner who lost every device.

- **First setup.** With no account file, a code like `XXXX-XXXX-XXXX` (12 characters from an alphabet
  without look-alikes) is made at start and held in memory. The first visit asks for it with a new
  username and password (12+ characters). Whoever cannot read the server's output cannot claim the
  dashboard, however they reached the port. A restart makes a new code; using it ends it.
- **The account file** (`dashboard.credentialsPath`, `dashboard-account.json` beside the executable;
  `/app/data/dashboard-account.json` in Docker) holds the username and a PBKDF2 hash in the same
  `pbkdf2-sha256$…` format as Raindrop passwords. Written atomically and mode `0600`. It is not in the
  database, so `database.wipeOnStartup` leaves it alone. One that cannot be read stops the server at
  start (a startup check) rather than refusing every sign-in.
- **Sessions** are in memory, as Raindrop's are: a restart signs the browser out. A random 256-bit token
  in a `__Host-raindrop-dashboard` cookie (`Secure`, `HttpOnly`, `SameSite=Strict`), ending after
  `sessionIdleMinutes` (30) idle or 12 hours in all. At most 32 browsers; the oldest makes room.
- **Throttled.** Setup, sign-in and password change are limited to 10 attempts a minute per address
  (`RequestThrottle.PerMinute`), which also bounds the CPU a password guesser can make PBKDF2 spend. A
  wrong username takes as long to refuse as a wrong password.
- **Forgot the password?** `dashboard-reset` in the console (after a `yes`) deletes the account file,
  signs every browser out and prints a new setup code. Deleting the file by hand and restarting does the
  same.
- **Changing the password** from the page needs the current one and signs out every other browser.

Every response carries a content security policy that lets the page load nothing but itself and run no
inline script, refuses framing and is never cached. A request that changes something must carry the
dashboard's own `Origin`. There is deliberately no `Strict-Transport-Security`: with a self-signed
certificate it would make the browser's warning impossible to click through.

What the owner does through the page is written to the server's output as `dashboard: …` lines —
setup, sign-ins and failed sign-ins (with the address), password changes, recovery codes issued and
cancelled. Never a password or a code.

## What it shows

The page polls every 5 seconds. A background sampler (`DashboardSampler`) takes a reading every 5
seconds and keeps an hour of them, in memory, for the charts.

| Card | Shows | From |
|---|---|---|
| Header | Running / reconnecting / not answering, version, uptime | `ServerBuildInfo` (the release version `release.yml` stamps, and the process start) |
| Server process | CPU (share of every core), memory (working set, .NET heap), threads, GC count; an hour of CPU and memory | `ProcessMetrics` |
| Machine *or* container | See below | `HostMetrics` / `ContainerMetrics` |
| Connected clients | Open event streams (one per running client), members online, signed-in sessions, people in voice calls and how many calls; an hour of connected clients | `IEventConnectionRegistry`, `ISessionStore.Count`, `IVoiceStateStore` |
| Accounts | Active, left with data kept, erased | `IUserStore.CountByStatusAsync` — a `COUNT(*)` in the relational stores |
| Server | Version, protocol, start time, .NET, database provider, voice provider, attachments, TLS and identity fingerprints | `ServerIdentity`, the registered options |
| Roles | Every role with its member count; an editor for its name and permissions; members, with remove and "give role" | `DashboardRoles` over `RoleService` — see [Roles](#roles) |
| Storage | Encrypted files and their size, uploads not yet sent, free space on each volume the server writes to | `IAttachmentStore.GetStatisticsAsync`, `DriveInfo` |

### Roles

The **Roles** card (in the Members section) is the owner's way to edit [roles](roles-and-permissions.md)
without a client. It lists the roles; opening one shows an editor with a checkbox per permission,
grouped by category, built from the server's own catalogue (`GET /api/permissions`), so it lists exactly
what this build knows. The `owner` role is read-only, the `everyone` role can have its permissions
edited but not be renamed or deleted, and deleting any other role asks for confirmation. The role's
members are listed with a remove button, and "give role" finds an account by username or id; the role
chips on an account's card open that role. It goes through the `*FromConsoleAsync` methods of
`RoleService`, so the built-in role rules, unique names and the last-owner guard still apply and the
same role events are published. Every change is written to the dashboard audit log.

### The machine

On a machine of its own the server shows the operating system, kernel, architecture, processor and
core count, CPU in use, memory in use against the total, load average (Linux and macOS) and how long the
machine has been up. None of this is in .NET, so each platform has a reader:

- **Linux** — `/proc/stat`, `/proc/meminfo` (`MemAvailable`), `/proc/loadavg`, `/proc/uptime`,
  `/proc/cpuinfo`, `/etc/os-release`.
- **macOS** — `host_statistics` / `host_statistics64` (CPU ticks, free + inactive + speculative pages),
  `sysctlbyname` (`hw.memsize`, `kern.boottime`, `machdep.cpu.brand_string`, `kern.osproductversion`),
  `getloadavg`.
- **Windows** — `GetSystemTimes`, `GlobalMemoryStatusEx`.

### Inside Docker

When the server runs in a container **only the container is shown**, under a "Running inside Docker"
banner — the machine underneath is not the owner's to act on from here, and what a container sees of
it is misleading. `ContainerDetector` decides once at start:

- **Docker** when `/.dockerenv` exists (Docker makes it in every container) or the process's cgroup
  names Docker (`/docker/<id>`, `docker-<id>.scope`). The mount table is deliberately not asked: a
  machine that merely *runs* Docker has `/var/lib/docker` mounts in its own.
- **A container** (Podman, Kubernetes) when only .NET's images say so, `DOTNET_RUNNING_IN_CONTAINER=true`.

The container card reads the cgroup — v2 (`cpu.max`, `cpu.stat`, `memory.current`, `memory.max`,
`pids.*`) or v1 (`cpu.cfs_quota_us`, `cpuacct.usage`, `memory.usage_in_bytes`, `memory.limit_in_bytes`)
— and shows the container id (from the mount of its own `/etc/hostname`), hostname, image OS, how long
it has run, CPU used against its limit, memory against its limit, processes, and network traffic
(`/proc/net/dev`, loopback excluded). "No limit" is said as such. The Docker socket is **not** mounted
— it would give the server root on the host — so the image name and labels are not shown.

## Recovering an account

The page's face of the console's `reclaim` ([E2EE](e2ee.md#reclaiming-an-account)), for a member who
lost every device, their recovery words and any backup. Nothing new underneath: it issues the same
one-time `RDC1-<account id>-<secret>` claim code that the member's new identity redeems when it joins.

1. The member gives the owner their **username or account id** (with or without an `@`).
2. The owner looks it up. The card shows the username, id, whether they are active or left with data
   kept, when they joined and last signed in, their roles, how many devices are registered, whether they
   are online, and whether a code is already waiting — enough to be sure it is the right account.
   Erased accounts and unknown names are "no member here".
3. **Issue a recovery code** asks for confirmation with the consequences spelled out (their devices are
   signed out, every member sees their safety number change, old messages stay unreadable), then shows
   the code once with its expiry (a day) and a copy button. Issuing another replaces it; a restart
   forgets it.
4. **Codes waiting to be used** lists every outstanding code (member, issued, works until) — never the
   code itself — with **Cancel code** to withdraw one given to the wrong person or no longer needed.

The console has the same: `reclaim <user>`, `reclaims` to list, `reclaim-cancel <user>`.

## API

All JSON, all on the dashboard's port, all scoped to it. Refusals are `{ "code", "message" }` with an
HTTP status for their kind (400, 401, 403 `cross_origin`, 404, 409, 429 `rate_limited`).

| Method and path | Needs | Does |
|---|---|---|
| `GET /`, `/app.js`, `/app.css` | — | The page, embedded in the assembly |
| `GET /api/state` | — | `{ setUp, signedIn, username }` |
| `POST /api/setup` | setup code | `{ code, username, password }` → creates the account, signs in |
| `POST /api/login` | — | `{ username, password }` → signs in |
| `POST /api/logout` | — | Signs this browser out |
| `POST /api/password` | session | `{ currentPassword, newPassword }` |
| `GET /api/status` | session | Everything above, read now |
| `GET /api/history?since=` | session | The sampler's readings, all of the last hour or those after `since` |
| `GET /api/accounts/lookup?q=` | session | One account by username or id |
| `POST /api/accounts/{id}/recovery` | session | Issues a recovery code; the only response that carries one |
| `GET /api/recoveries` | session | Outstanding codes, without the codes |
| `DELETE /api/recoveries/{id}` | session | Cancels one |
| `GET /api/permissions` | session | The permission catalogue: name, title, category, summary |
| `GET /api/roles` | session | Every role, each `{ role, members }` with `members` a count |
| `GET /api/roles/{id}` | session | `{ role, members: [{ userId, username }] }` |
| `POST /api/roles` | session | `{ name, permissions?, position? }` → creates a role |
| `PATCH /api/roles/{id}` | session | Any of `name`, `permissions`, `position`; an absent field is kept, an empty `permissions` list clears them |
| `DELETE /api/roles/{id}` | session | Deletes the role and takes it from everyone |
| `POST /api/accounts/{userId}/roles/{roleId}` | session | Gives the account the role |
| `DELETE /api/accounts/{userId}/roles/{roleId}` | session | Takes it away |

The role endpoints, like the rest, need a signed-in session and a same-origin request.

## Tests

`DashboardTests.cs`: the allowlist, the options and the listener's validation, the account (setup,
sign-in, idle and absolute session limits, password change, reset, an unreadable file), the `/proc` and
cgroup v1/v2 readers and Docker detection against files written the way the kernel writes them, and the
whole dashboard over `TestServer` (the listener's port simulated by a startup filter): which port answers
what, nothing before sign-in, a forged origin refused, the counts matching what was registered, and
issuing, listing and cancelling a recovery code that the server then accepts. `RoleTests.cs` has
`DashboardRoleTests`: the role endpoints refuse without a sign-in or the dashboard's own origin, and the
owner creates, edits, assigns and deletes a role through them. `DeviceAuthTests.cs` covers
issuing by account id, listing, cancelling and lapsing against real `CompleteAuth` redemptions.
