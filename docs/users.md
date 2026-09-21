# Users

Domain code: [`RaindropServer/Users/`](../RaindropServer/Users/). HTTP surface:
[`ServerCli/Endpoints/Users/`](../ServerCli/Endpoints/Users/). `UserService` owns every rule, so
endpoints stay thin and database drivers stay dumb. Request/response contracts live in the library
next to `UserService` (in [`Contracts.cs`](../RaindropServer/Users/Contracts.cs)) since the library
returns them and cannot depend on `ServerCli`.

## Lifecycle

The state machine to keep straight is [`UserStatus`](../RaindropServer/Users/UserStatus.cs):
`unregistered` → `active` → (`left_preserved` or `erased`), with `left_preserved` able to return to
`active` via rejoin.

- **First login is registration.** A client arrives with only its GUID, claims an unused username,
  sets a password, and gets a session back in the same call — there is no separate "create account"
  step before that.
- **Usernames are unique per server**, compared on a lower-cased `username_normalized` column with a
  unique index. The same person can be `alice` on one server and `alice42` on another.
- **Sessions live in memory only** ([`InMemorySessionStore`](../RaindropServer/Users/InMemorySessionStore.cs))
  and are deliberately never persisted — a server restart logs everyone out. Clients log out when they
  close; a 12-hour idle timeout is the backstop (`UserOptions.SessionIdleTimeout`).
- **Leaving preserves or erases**, the caller's choice (`LeaveRequest.PreserveData`):
  - `LeftDataPreserved` keeps the username (still reserved, nobody else can claim it) and password
    hash until `data_expires_at`, capped at two years by `UserOptions.MaximumDataRetention`.
    [`RetentionCleanupService`](../RaindropServer/Users/RetentionCleanupService.cs) sweeps expired
    preserved accounts on a timer and at startup.
  - `Erased` nulls the username and password hash immediately.
- **The UUID row always survives**, whichever path was taken. Messages are linked to the UUID, so an
  erased account leaves a tombstone row rather than a deleted one — and that same UUID may register
  again from scratch later. The same goes for the copies of their *deleted* messages in the
  [archive](messages.md#the-deleted-message-archive): erasing an account does not erase them.

## Password hashing

PBKDF2-HMAC-SHA256, 210,000 iterations, per-password salt
([`Pbkdf2PasswordHasher`](../RaindropServer/Users/Pbkdf2PasswordHasher.cs)), stored self-describing as:

```
pbkdf2-sha256$<iterations>$<salt>$<hash>
```

Storing the iteration count in the hash string means the work factor can be raised later without
invalidating every existing password.

## User API

All routes are under `/api/users` (plus one under `/api/server`). Authenticated routes take
`Authorization: Bearer <token>`.

| Method | Route | Auth | Purpose |
|---|---|---|---|
| GET | `/api/server/info` | – | Handshake: protocol version, TLS fingerprint to pin, supported wire formats |
| POST | `/api/users/status` | – | What this UUID means here: `unregistered`, `active`, `left_preserved`, `erased` |
| POST | `/api/users/username-available` | – | Uniqueness pre-check for the registration screen |
| POST | `/api/users/register` | – | First login: claim a username and password, get a session |
| POST | `/api/users/login` | – | UUID (or username) + password → session |
| POST | `/api/users/rejoin` | – | Restore a preserved account |
| POST | `/api/users/logout` | ✓ | End this session |
| GET | `/api/users/me` | ✓ | Own profile |
| POST | `/api/users/lookup` | ✓ | Resolve a UUID to a display name, e.g. for a message author |
| POST | `/api/users/me/password` | ✓ | Change password; revokes all sessions and returns a new token |
| POST | `/api/users/me/username` | ✓ | Claim a different username |
| POST | `/api/users/me/leave` | ✓ | Leave the server, preserving or erasing data |

Endpoint implementations: [`ServerCli/Endpoints/Users/`](../ServerCli/Endpoints/Users/) — one file per
route (`RegisterEndpoint.cs`, `LoginEndpoint.cs`, `RejoinEndpoint.cs`, `LogoutEndpoint.cs`,
`CurrentUserEndpoint.cs`, `LookupUserEndpoint.cs`, `ChangePasswordEndpoint.cs`,
`ChangeUsernameEndpoint.cs`, `LeaveServerEndpoint.cs`, `UserStateEndpoint.cs`,
`UsernameAvailabilityEndpoint.cs`).

### Request/response shapes

Defined in [`RaindropServer/Users/Contracts.cs`](../RaindropServer/Users/Contracts.cs):

- `UserProfile(UserId, Username, State, CreatedAtUtc, LastLoginAtUtc)` — the caller's own account.
- `PublicUserProfile(UserId, Username?, State)` — what another member may see about a UUID (e.g. to
  render a message author); `Username` is null once the account is erased.
- `SessionResponse(User, Token, ExpiresAtUtc)` — returned by register/login/rejoin.
- `RegisterRequest(UserId, Username, Password)`
- `LoginRequest(UserId?, Username?, Password)` — either identifier works.
- `RejoinRequest(UserId, Password)`
- `ChangePasswordRequest(CurrentPassword, NewPassword)`
- `ChangeUsernameRequest(Username)`
- `LeaveRequest(Password, PreserveData)`
- `UserStateResponse(UserId, State, Username?, DataExpiresAtUtc?)`
- `UsernameAvailabilityResponse(Username, Available, Reason?)`
- `LeaveResponse(State, DataExpiresAtUtc?, Message)`

### Error codes

`{ "error": "<code>", "message": "<text>" }`. Codes a client branches on: `username_taken`,
`already_registered`, `account_preserved`, `account_left`, `invalid_credentials`, `invalid_session`,
`no_preserved_account`, `weak_password`, `invalid_username`, `invalid_payload`,
`unsupported_media_type`.

Login deliberately answers `invalid_credentials` for both an unknown UUID and a wrong password, so the
API cannot be used to enumerate who is registered on the server.

## Session listeners and events

[`ISessionListener`](../RaindropServer/Users/ISessionListener.cs) is the counterpart of
`IRegistrationListener` for sessions: `UserService` calls every registered one after a member's
sessions change — a session opened (register, login, rejoin, password change), one ended (logout), or
all of them revoked (password change, leaving). The roster uses it to announce presence, and the event
hub uses it to close streams whose token has just been revoked. Like registration listeners, one that
throws is reported and stepped over.

`UserService` also publishes `member.joined` (register, rejoin), `member.renamed` and `member.left`
itself — see [Events](events.md).

`ISessionStore.IsActive(token)` answers whether a token is still live **without** sliding its idle
timeout, which `TryValidate` does. The event hub asks it before every delivery; sliding there would let
a busy server keep an abandoned session alive just by talking to it. An open event stream *does* renew
its session on a timer, deliberately — a client with a stream open is still there.

## Configuration

See [Configuration](configuration.md#users) for the `users` section of `raindrop.json`
(`dataRetentionDays`, `sessionIdleTimeoutHours`, `minimumPasswordLength`).

## Related

- [Members](members.md) — the roster is derived from active users plus live sessions.
- [Events](events.md) — what a connected client is told when members join, leave or log in.
- [Roles & permissions](roles-and-permissions.md) — the first UUID to register is granted the owner
  role automatically.
