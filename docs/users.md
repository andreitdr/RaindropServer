# Users

Domain code: [`RaindropServer/Users/`](../RaindropServer/Users/). Wire surface: `raindrop.v1.UserService`
([`users.proto`](../../RaindropProto/raindrop/v1/users.proto)), served by
[`UserGrpcService`](../RaindropServer.Grpc/Services/AccountServices.cs). `UserService` owns every rule, so
the gRPC service stays thin and database drivers stay dumb. The domain's contracts live in the library
next to `UserService` (in [`Contracts.cs`](../RaindropServer/Users/Contracts.cs)); the gRPC layer maps
them to protobuf (see [gRPC services](grpc-services.md)).

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
  close; a 12-hour idle timeout is the backstop (`UserOptions.SessionIdleTimeout`). An open event
  stream renews its session; a lapsed one is forgotten when its token is next used, or by
  `SessionSweepService` within ten minutes (`ISessionStore.RemoveExpired`).
- **Leaving preserves or erases**, the caller's choice (`LeaveRequest.PreserveData`):
  - `LeftDataPreserved` keeps the username (still reserved, nobody else can claim it) and password
    hash until `data_expires_at`, capped at two years by `UserOptions.MaximumDataRetention`.
    [`RetentionCleanupService`](../RaindropServer/Users/RetentionCleanupService.cs) sweeps expired
    preserved accounts on a timer and at startup.
  - `Erased` nulls the username and password hash immediately.
  - Before either is written, every registered [`ILeaveGuard`](../RaindropServer/Users/ILeaveGuard.cs)
    may refuse the leave by throwing. The roles subsystem's `OwnerLeaveGuard` refuses an *erasing*
    leave by the only owner (`last_owner`): nobody could ever log in as that UUID again, so the server
    would be left without an owner. A preserved leave is allowed, because the owner can rejoin.
- **A client that lost its UUID can still come back.** Login and rejoin both accept the username in
  place of the UUID, and answer with the account's real UUID for the client to adopt. This is how a
  member who removed the server from their client (which forgets the GUID) logs back in as the same
  member, with the same roles and messages.
- **The UUID row always survives**, whichever path was taken. Messages are linked to the UUID, so an
  erased account leaves a tombstone row rather than a deleted one — and that same UUID may register
  again from scratch later. The same goes for the copies of their *deleted* messages in the
  [archive](messages.md#the-deleted-message-archive): erasing an account does not erase them.

## Signing in with a device key

`E2ee/DeviceAuthService`, served as `UserService.BeginAuth` and `CompleteAuth` (both anonymous). **This is
how the client signs in. The password calls still exist beside it but the app no longer calls them;
removing them is still to do.**
An account belongs to whoever holds a device key that the account's identity key has certified.

1. `BeginAuth(user_id)` returns a 32-byte nonce, good once, for `E2eeOptions.ChallengeLifetime`. The answer
   is the same whether or not the UUID has an account. At most 20,000 challenges are outstanding at once
   (`auth_busy` beyond that, after lapsed ones are dropped).
2. The device signs `"raindrop/auth/v1\n" ‖ u16 length of this server's identity key ‖ that key ‖ nonce`
   with its device key (`DeviceAuthService.SignedBytes`, mirroring `Session::sign_auth_challenge` in the
   Rust core). The server's own key is in there so a challenge relayed between servers is useless.
3. `CompleteAuth` uses the challenge up, verifies the signature, and then:

| The device is… | Needs | What happens |
|---|---|---|
| Registered and active | signature | Signs in |
| New to an active account | signature + certificate | The certificate must be signed by the account's identity key (`identity_mismatch` otherwise); the device is registered, then signs in |
| The first for a UUID with no account (or an erased one) | signature + certificate + username | The account is created with **no password**, the certificate establishes its identity key |
| Anything on an account that left with data preserved | as above | The account is restored |

The session is **bound to the device from the start** (`UserService.OpenDeviceSessionAsync`), so there is
no separate `BindSession` step. Other sessions of the account are left alone.

Refusals that would say whether a UUID or device exists are all `invalid_credentials`: an unknown device
with no certificate, a certificate about another user or device, a bad signature, a revoked device
presenting only its signature, and an account that has a password but has never had a device — letting a
certificate establish *that* account's identity would hand it to whoever asked first. A server with no
identity key answers `auth_unavailable`.

A device new to an account that has an authenticator must also send `authenticator_code`
(`authenticator_required`, `invalid_authenticator_code`, `authenticator_locked`), unless it has waited
out a recovery started with `RequestRecovery` (`recovery_pending` until then, `recovery_cancelled` if one
of the account's devices said no). All three calls are rate-limited per address — see
[E2EE](e2ee.md#recovering-without-the-authenticator).

Not built yet: removing the password calls. `LeaveServer` and `ResetIdentity` already accept a
device-bound session in place of a password for an account that has none. See the [roadmap](roadmap.md).

## Password hashing

PBKDF2-HMAC-SHA256, 210,000 iterations, per-password salt
([`Pbkdf2PasswordHasher`](../RaindropServer/Users/Pbkdf2PasswordHasher.cs)), stored self-describing as:

```
pbkdf2-sha256$<iterations>$<salt>$<hash>
```

Storing the iteration count in the hash string means the work factor can be raised later without
invalidating every existing password.

## User API

`raindrop.v1.UserService`, plus the handshake in `raindrop.v1.ServerService`. Authenticated RPCs take
`authorization: Bearer <token>` metadata.

| RPC | Auth | Purpose |
|---|---|---|
| `ServerService.GetServerInfo` | – | Handshake: protocol version, TLS fingerprint to pin, identity key, capabilities |
| `GetUserStatus` | – | What this UUID means here: `USER_STATE_UNREGISTERED`, `_ACTIVE`, `_LEFT_PRESERVED`, `_ERASED` |
| `CheckUsername` | – | Uniqueness pre-check for the registration screen |
| `Register` | – | First login: claim a username and password, get a session |
| `Login` | – | UUID (or username) + password → session |
| `Rejoin` | – | Restore a preserved account, by UUID or username |
| `Logout` | ✓ | End this session |
| `GetMe` | ✓ | Own profile |
| `LookupUser` | ✓ | Resolve a UUID to a display name, e.g. for a message author |
| `ChangePassword` | ✓ | Change password; revokes all sessions and returns a new token |
| `ChangeUsername` | ✓ | Claim a different username |
| `LeaveServer` | ✓ | Leave the server, preserving or erasing data |

### Domain contracts

Defined in [`RaindropServer/Users/Contracts.cs`](../RaindropServer/Users/Contracts.cs); their protobuf
counterparts are `UserProfile`, `PublicUserProfile`, `Session` and each RPC's request and response in
`users.proto`:

- `UserProfile(UserId, Username, State, CreatedAtUtc, LastLoginAtUtc)` — the caller's own account.
- `PublicUserProfile(UserId, Username?, State)` — what another member may see about a UUID (e.g. to
  render a message author); `Username` is null once the account is erased.
- `SessionResponse(User, Token, ExpiresAtUtc)` — returned by register/login/rejoin.
- `RegisterRequest(UserId, Username, Password)`
- `LoginRequest(UserId?, Username?, Password)` — either identifier works.
- `RejoinRequest(UserId?, Username?, Password)` — either identifier works.
- `ChangePasswordRequest(CurrentPassword, NewPassword)`
- `ChangeUsernameRequest(Username)`
- `LeaveRequest(Password, PreserveData)`
- `UserStateResponse(UserId, State, Username?, DataExpiresAtUtc?)`
- `UsernameAvailabilityResponse(Username, Available, Reason?)`
- `LeaveResponse(State, DataExpiresAtUtc?, Message)`

### Error codes

A refusal is a gRPC status whose `ErrorInfo.reason` is the code (see
[gRPC services](grpc-services.md#errors)). Codes a client branches on: `username_taken`,
`already_registered`, `account_preserved`, `account_left`, `invalid_credentials`, `missing_token`,
`invalid_session`, `no_preserved_account`, `missing_identifier`, `last_owner`, `weak_password`,
`invalid_username`, `invalid_payload`.

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
