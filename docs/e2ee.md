# End-to-end encryption: identities, devices and the delivery service

Raindrop's end-to-end encryption uses MLS (RFC 9420), run entirely on clients by the Rust core in
[`RaindropCrypto/`](../../RaindropCrypto/docs/README.md). The server never holds a private key. This
page covers what the server *does* hold and do:

- **The identity layer:** users' public identity keys, their devices, which session runs on which
  device, sealed key backups, and the relay for linking a new device.
- **The MLS delivery service:** ordering, storing and fanning out the traffic of each channel's MLS
  group, none of which it can read. See [The delivery service](#the-delivery-service) below.

**Status:**

| Part | State |
|---|---|
| Identity layer | Implemented (this page) |
| MLS delivery service | Implemented (this page) |
| Encrypted text messages | Implemented — see [Messages](messages.md#end-to-end-encryption) |
| Encrypted voice | Implemented — see [Voice](voice.md#end-to-end-encrypted-calls) |

What stays visible to the server is metadata: who talks in which channel and call, when, and roughly
how much.

Code: [`RaindropServer/E2ee/`](../RaindropServer/E2ee), endpoints in
[`ServerCli/Endpoints/E2ee/`](../ServerCli/Endpoints/E2ee). Capabilities in `GET /api/server/info`:
`e2ee.devices.v1` (identities), `e2ee.delivery.v1` (the delivery service) and `e2ee.voice.v1` (calls
are end-to-end encrypted, so clients refuse to join one they cannot encrypt).

## The model

- **One identity key per user** (Ed25519). The private half is only ever on the user's devices and
  inside their sealed backup. The server stores the public half so other users can fetch it, and
  clients pin it (trust on first use, with safety numbers to verify).
- **One device record per device.** A device has its own Ed25519 MLS signature key and a
  **certificate** in which the identity key binds that key to `(user id, device id)`. The format is in
  [`DeviceCertificate`](../RaindropServer/E2ee/DeviceCertificate.cs), and the same bytes go into the
  device's MLS credential.
- **The first device a user registers establishes their identity key.** After that, a certificate
  signed by any other key is refused with `identity_mismatch`.
- **A password opens a session; a device key binds it.** A session proves which device it runs on by
  signing a one-time challenge. Changing what the account's encryption depends on (revoking another
  device, replacing the backup, approving a link) needs a bound session. Somebody holding only the
  password therefore cannot remove the real owner's devices or overwrite their backup.

The server's checks are **defence in depth, not the guarantee**. Clients verify every certificate and
identity key themselves against what they pinned, so a server that lies about keys is caught by the
clients, not trusted.

## What the server can and cannot do

| It can | It cannot |
|---|---|
| Refuse to store or deliver things (denial of service) | Sign as a user or read what they encrypt |
| See who has how many devices, and when they were added | Add a device to a user whose identity key is pinned: it cannot produce the certificate |
| Let an account holder **reset** their identity with the password | Make that reset silent: every member is sent `e2ee.identity.reset`, and peers' clients see the safety number change |

## Linking a new device

A user adding a device while another still works moves their identity secret across without the
server being able to read or substitute it. The cryptography is in
[`RaindropCrypto/crates/raindrop_crypto/src/link.rs`](../../RaindropCrypto/docs/device-linking.md).

1. **New device** (password session): makes its device key and an X25519 key, uploads the *offer*
   (`device-link/start`), and shows a **link code**: `RDL1-<link id>-<commitment>`. The commitment is
   128 bits of SHA-256 over the offer.
2. **Existing device** (bound session): the user enters the code. The device fetches the offer
   (`device-link/offer`) and refuses it unless it matches the commitment, so a substituted offer is
   caught. It then certifies the new key and encrypts the identity secret and pins to the offer's
   X25519 key (`device-link/approve`).
3. **New device** polls `device-link/approval`, which delivers the approval once and only to the
   session that opened the link. It opens the approval, registers its certificate, and binds.

Links live in memory (`IDeviceLinkRelay`, default `InMemoryDeviceLinkRelay`) for 5 minutes.

## Key backup

`backup/put` stores one sealed blob per user, up to 64 KiB, sealed under a 24-word recovery key or an
Argon2id passphrase. It needs a bound session. `GET /api/e2ee/backup` returns it to any password
session, because a device restoring from it has no key to bind with yet, and the blob is useless
without the recovery key.

## API

All authenticated. Byte fields are base64 in JSON and binary in MessagePack.

| Method | Route | Needs | Purpose |
|---|---|---|---|
| POST | `/api/e2ee/devices/register` | session | `{certificate}` registers a device. The first one sets the identity key. Idempotent. |
| POST | `/api/e2ee/devices/list` | session | `{userIds}` (≤ 500) returns each user's `identityKey` and active `devices` (`deviceId`, `certificate`, `createdAtUtc`) |
| POST | `/api/e2ee/devices/revoke` | bound, unless revoking this session's own device | `{deviceId}` revokes it and ends its sessions |
| POST | `/api/e2ee/identity/reset` | session + `{password}` | forgets the identity key and backup and revokes every device |
| POST | `/api/e2ee/session/challenge` | session | returns `{nonce, expiresAtUtc}`: 32 bytes, 2 minutes, single use |
| POST | `/api/e2ee/session/bind` | session | `{deviceId, signature}`: Ed25519 by the device key over `"raindrop/session-binding/v1\n" ‖ nonce` |
| GET | `/api/e2ee/backup` | session | returns `{blob, updatedAtUtc}`, `blob` null when there is none |
| POST | `/api/e2ee/backup/put` | bound | `{blob}` replaces the backup |
| POST | `/api/e2ee/device-link/start` | session | `{offer}` (≤ 4 KiB) returns `{linkId, expiresAtUtc}` |
| POST | `/api/e2ee/device-link/offer` | bound | `{linkId}` returns `{offer}` |
| POST | `/api/e2ee/device-link/approve` | bound | `{linkId, approval}` (≤ 8 KiB) |
| POST | `/api/e2ee/device-link/approval` | the session that started the link | `{linkId}` returns `{approval}`, null until approved, delivered once |

**The delivery service.** All need a bound session.

| Method | Route | Purpose |
|---|---|---|
| POST | `/api/e2ee/groups/create` | `{channelId, groupInfo}` starts the channel's group (`group_exists` if it has one) |
| POST | `/api/e2ee/groups/state` | `{channelId}` returns `{epoch, groupInfo, groupInfoEpoch}` (`group_not_found` before creation) |
| POST | `/api/e2ee/groups/commit` | `{channelId, commit, welcome?, welcomeRecipients?}` returns the new `{epoch}`, or `epoch_conflict` |
| POST | `/api/e2ee/groups/info` | `{channelId, groupInfo}` publishes the group info for the current epoch |
| POST | `/api/e2ee/groups/sync` | `{channelId, fromEpoch}` returns `{epoch, commits: [{epoch, message}], resyncRequired}` (≤ 500 per page) |
| POST | `/api/e2ee/groups/roster` | `{channelId}` returns `{devices: [{userId, deviceId}]}`, who should be in the group |
| POST | `/api/e2ee/keypackages/upload` | `{keyPackages, lastResort?}` returns `{oneTime}` |
| GET | `/api/e2ee/keypackages/count` | returns `{oneTime}` for this device |
| POST | `/api/e2ee/keypackages/claim` | `{channelId, devices}` returns one `{userId, deviceId, keyPackage}` per device that has one and belongs in the group; others are skipped |
| GET | `/api/e2ee/welcomes` | Welcomes for this device: `{welcomeId, channelId, epoch, welcome}` |
| POST | `/api/e2ee/welcomes/ack` | `{welcomeIds}` forgets processed Welcomes |

**Error codes:**
- certificates and identity: `invalid_certificate`, `certificate_mismatch`, `identity_mismatch`
- devices: `device_exists`, `too_many_devices` (10 per user), `device_not_found`
- session binding: `device_required`, `no_challenge`, `invalid_signature`
- lookups and payloads: `too_many_users`, `invalid_backup`, `invalid_link`, `link_not_found`
- delivery: `group_exists`, `group_not_found`, `epoch_conflict`, `wrong_group`, `invalid_mls_message`,
  `invalid_welcome`, `not_allowed_in_group`, `too_many_key_packages`, `too_many_devices`,
  `channel_not_found`

## Events

Open-string types. The first three go to every connected member:

| Type | Data |
|---|---|
| `e2ee.device.added` | `DeviceEvent(UserId, DeviceId, Certificate)` |
| `e2ee.device.revoked` | `DeviceEvent(UserId, DeviceId, Certificate)` |
| `e2ee.identity.reset` | `IdentityResetEvent(UserId)` |
| `e2ee.handshake` | `HandshakeEvent(ChannelId, Epoch, Message)`: an accepted commit. Sent to the group's policy audience, not to everyone. |
| `e2ee.welcome` | `WelcomeEvent(ChannelId, DeviceId)`, to the recipient only |
| `e2ee.group.reconcile` | `GroupReconcileEvent(ChannelId)`, to the group's audience |
| `e2ee.keypackages.low` | `KeyPackagesLowEvent(DeviceId, Remaining)`, to the owner only |

## Storage

Three tables, created like every other (see [Database modules](database-modules.md)):

- `raindrop_user_identities(user_id PK, identity_key, updated_at)`
- `raindrop_devices(user_id, device_id, signature_key, certificate, created_at, revoked_at, PK(user_id, device_id))`.
  Revoked rows are kept, so a revoked device id cannot be registered again.
- `raindrop_key_backups(user_id PK, blob_data, updated_at)`

The delivery service adds four more:

- `raindrop_mls_groups(channel_id PK, epoch, group_info, group_info_epoch, updated_at)`
- `raindrop_mls_commits(channel_id, epoch, message, sender_user_id, created_at, PK(channel_id, epoch))`
- `raindrop_mls_key_packages(package_id PK, user_id, device_id, key_package, last_resort, created_at)`
- `raindrop_mls_welcomes(welcome_id PK, user_id, device_id, channel_id, epoch, welcome, created_at)`

Deleting a channel drops its group, log and Welcomes.

Only public keys and opaque blobs are stored. A member whose account is **erased** has all three
cleared (`ErasedMemberCleanup`). A member who leaves with their data preserved keeps them for a
return.

## The delivery service

[`MlsDeliveryService`](../RaindropServer/E2ee/MlsDeliveryService.cs). Every channel has at most one MLS
group, named by the channel: its group id is the channel UUID as 16 big-endian bytes. The server is
never a member of any group.

### What it can see

[`MlsFraming`](../RaindropServer/E2ee/MlsFraming.cs) reads only the cleartext MLS envelope:
- the wire format
- for group messages: the group id, the epoch, the content type (application, proposal or commit)
  and, for public messages only, the sender type
- for a group info: the group id and epoch

Everything else is opaque bytes. Key packages, Welcomes and group infos must be RFC 9420 `MLSMessage`s,
so a key package is wrapped with wire format `key_package`, not bare.

### The rules

- **One commit per epoch.** A commit is accepted only if it was made in the group's current epoch.
  Accepting it advances the epoch and appends it to the log in one transaction. The log's key
  `(channel_id, epoch)` makes the database itself refuse a second commit for an epoch. The loser gets
  `409 epoch_conflict`: it discards its pending commit, syncs, processes the winner, and retries if the
  change is still needed. So every member sees the same epochs and a group cannot fork.
- **Only devices take part.** Every call needs a session [bound to a device](#the-model), so a password
  alone cannot read a group's commits or claim anybody's key packages. A revoked device is refused even
  if a session of it somehow survived.
- **Access is the membership policy's.** [`IE2eeMembershipPolicy`](../RaindropServer/E2ee/IE2eeMembershipPolicy.cs)
  decides who belongs in a channel's group. By default that is everyone with `view_channel`, plus
  `connect` for a voice channel. The policy is consulted:
  - on every request (anybody else gets `channel_not_found`, as elsewhere)
  - for every fan-out
  - for every Welcome delivered
  - for every device claimed or welcomed

  Somebody who loses access stops receiving the group's traffic **immediately**.
- **Removal is the members' job.** The server cannot change a group. When roles, memberships or
  devices change, or a member joins or leaves, `GroupMembershipReconciler` sends
  `e2ee.group.reconcile` to every group's members. They compare the tree with `groups/roster` (every
  device that should be in the group) and commit the difference:
  - removing leaves that should not be there, which rotates the keys past anyone removed
  - adding devices that should be there

  A revoked device's key packages and Welcomes are dropped at once.
- **The log is bounded** to `MaximumLogEpochs` (1000) epochs. A device further behind gets
  `resyncRequired` from `groups/sync` and rejoins by external commit, missing the messages in between,
  which forward-only history means it would anyway.

### Joining

- **Being added.** A member claims one key package per device (`keypackages/claim`), commits the add,
  and submits the commit with its Welcome and the list of recipient devices. The server needs that
  list because it cannot read the Welcome. Recipients must be on the roster; each finds the Welcome at
  `welcomes` (an `e2ee.welcome` event says so) and acknowledges it.
- **Joining alone.** A device fetches the latest group info (`groups/state`), makes an external commit,
  and submits it like any other commit. After any merged commit, a member publishes a group info for
  the new epoch (`groups/info`); one for any other epoch is refused.

**What the server cannot check:** which devices a member's (encrypted) commit adds. Clients check
every added device against the roster and against the identity key they pinned. That, and showing
members every device that joins, is what keeps a malicious member or server from adding one unnoticed.

### Key packages

Each device keeps up to 200 one-time key packages on the server (uploads of up to 100), plus one
reusable *last resort* package, which is handed out only when the one-time ones run out and is never
consumed. Below 10 left, the owner gets `e2ee.keypackages.low`.

## Seams for plugins

- `IDeviceStore` and `IKeyBackupStore` are created by the database driver. `IDatabaseDriver` has
  default implementations that refuse, so a driver written before this still loads.
- `IDeviceLinkRelay` is `TryAdd`, so a plugin can hold links somewhere shared.
- `IDeviceListener` (`TryAddEnumerable`) is told when a device is added or revoked, or an identity is
  reset. The MLS delivery service will use it to remove revoked devices from groups.
- `ISessionStore.BindDevice` / `RemoveAllForDevice` have refusing defaults, so an older session store
  still loads.
- `UserSession.DeviceId` is how any endpoint can tell whether its caller is bound.
- `IE2eeMembershipPolicy` (`TryAdd`) decides who belongs in a channel's group.
- `IMlsGroupStore`, `IKeyPackageStore` and `IWelcomeStore` are driver factories with refusing defaults.
- `E2eeOptions`:
  - identities: devices per user, challenge lifetime, link lifetime, backup size, users per listing
  - delivery: message size, key packages per device and per upload, low watermark, Welcome recipients,
    log length, sync page size

Ed25519 comes from BouncyCastle, wrapped in
[`Security/Ed25519Signatures`](../RaindropServer/Security/Ed25519Signatures.cs), the only file that
references it, because .NET has no built-in Ed25519.
