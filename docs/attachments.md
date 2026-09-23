# Attachments

Members can add files of any type to text-channel messages. Code: `RaindropServer/Attachments/` (the
domain), `ServerCli/Endpoints/Attachments/` (the HTTP surface), `Data/Relational/RelationalAttachmentStore.cs`.

## The model in one paragraph

Files are **end-to-end encrypted like the messages that carry them**. The sender's client encrypts each
file with a fresh random key in the `raindrop/attachment/v1` format (defined by the Rust core, see
[RaindropCrypto's attachments doc](../../RaindropCrypto/docs/attachments.md)), uploads the ciphertext in
chunks, and then posts an ordinary encrypted message (`send-encrypted`) whose MLS payload holds the
caption and, for each file, its id, key, name, MIME type, size and ciphertext hash. The request also
lists the attachment ids **in cleartext**, so the server can tie the stored blobs to the message for
access control and deletion. Recipients download the ciphertext and decrypt it on their device. The
server never sees a file's name, type or contents; it learns only **which channel it is for, its
declared extension, and its size**, which is exactly what the owner's rules are enforced on.

## What the server can enforce, and how

| Rule (`raindrop.json`) | Enforced on | Trust |
|---|---|---|
| `maximumFileSizeMegabytes`, `…ByExtension` | the declared ciphertext length, which **fixes the plaintext length exactly** (`AttachmentFormat`: `ciphertext = 20 + plaintext + 16 × max(1, ⌈plaintext / 65536⌉)`); an impossible length is refused, and the bytes that arrive can never exceed it | exact, trusts nothing |
| `allowedExtensions` / `blockedExtensions` / `allowFilesWithoutExtension` | the extension the upload declares | a modified client could lie. The receiving client compares the declared extension with the file name inside the encrypted message and refuses to preview a file whose two disagree |
| `maximumFilesPerMessage`, `maximumPendingUploadsPerMember`, `pendingUploadExpiryHours` | the server's own records | exact |
| `preview.*` | nothing; served to clients through `GET /api/attachments/policy` | a hint for clients, which render previews themselves (the server cannot make a thumbnail of a file it cannot read, so the **sender** uploads one as its own `thumbnail` attachment) |

A blocklist is a courtesy rather than a defence (any file can be renamed), which is why clients never
open a received file by themselves either.

## Lifecycle

```text
uploads/create ──► uploading ──chunk…chunk──► uploads/complete ──► uploaded ──send-encrypted──► attached
                        │                                              │                          │
                        └───────── uploads/cancel, or pendingUploadExpiryHours ──► deleted ◄── message or channel deleted
                                                                                    │
                                                                 AttachmentMaintenanceService removes the file
```

1. **Create** (`POST /api/attachments/uploads/create`) — resolves the channel (404 when hidden, as for
   messages), requires `send_messages` **and** `attach_files` there and a **bound session**
   (`device_required`), checks the declared extension and size against every `IAttachmentPolicy`, and
   reserves an id. Answers with an `UploadStatus`.
2. **Chunks** (`PUT /api/attachments/uploads/chunk`) — raw bytes, with `Raindrop-Attachment-Id` and
   `Raindrop-Upload-Offset` headers (ids stay out of the URL, like everywhere else). The offset must be
   exactly what the server has received (`upload_offset_mismatch` otherwise); a chunk carries at most
   `uploadChunkBytes` (8 MiB) and never runs past the declared size (`chunk_too_large`, 413). A chunk
   that fails part-way is simply sent again from the same offset: storage truncates to it. Chunks of one
   upload are serialised in-process.
3. **Resume** (`POST /api/attachments/uploads/status`) — how many bytes have arrived.
4. **Complete** (`POST /api/attachments/uploads/complete`) — every byte must be there
   (`upload_incomplete`) and hash to the SHA-256 the uploader computed; a mismatch throws the upload
   away (`hash_mismatch`). The file is then committed in storage and becomes `uploaded`. Completing
   twice is not an error.
5. **Send** — `POST /api/channels/text/messages/send-encrypted` with `attachmentIds`. The files are
   claimed for the new message **before** it is stored, atomically and all-or-nothing (a conditional
   `UPDATE … WHERE state = 'uploaded'` per file in one transaction), so two messages can never carry
   one file; if storing the message fails, they are released again. Every file must be the caller's,
   finished, uploaded for this channel, and not already sent. At most `maximumFilesPerMessage` files,
   plus at most one thumbnail per file. `attach_files` is checked again here, since it may have been
   taken away since the upload. Plaintext `send` cannot carry files.
6. **Read** — `ChannelMessage.attachments` lists `{attachmentId, kind, extension, ciphertextSize,
   ciphertextSha256}` in history and in `message.created`; `POST /api/attachments/download` streams the
   ciphertext to anybody who can see the channel (the uploader alone, before it is sent). Everything
   else — an unknown id, a hidden channel, somebody else's unsent upload — is the same
   `attachment_not_found`, so ids cannot be probed.
7. **Delete** — `AttachmentDeletionListener` (an `IMessageDeletionListener`) marks a deleted message's
   files, or a deleted channel's, `deleted`. That is quick and idempotent, as the fail-closed deletion
   seam requires. `AttachmentMaintenanceService` then removes the files and the rows. Attachments are
   **not** copied into the deleted-message archive: they are ciphertext whose key went with the message.

`AttachmentMaintenanceService` runs at startup and every `MaintenanceInterval` (10 minutes): it expires
uploads older than `pendingUploadExpiryHours` that were never sent, removes the files of `deleted`
records, and — at startup and daily — removes stored files no record points at (left by a crash between
writing and recording), once they are older than the upload expiry.

## Storage

`IAttachmentStorage` holds the bytes; the default `LocalFolderAttachmentStorage` keeps them under
`attachments.storagePath` (relative to the executable; `/app/data/attachments` in Docker, on the
mounted volume) in two levels of subfolders named after each key (`ab/cd/abcd…`). An upload in progress
is `<key>.part` and becomes the file by a rename on completion, so a reader never sees half a file. Keys
are 32 hex digits and anything else is refused, so a key cannot walk out of the folder. Everything
stored is ciphertext.

**Back the folder up with the database**: the database says which message each file belongs to, and
the folder holds the file.

`AttachmentStorageStartupCheck` makes sure the folder can be written to before the server listens, and
warns when the disk under it has less room than the largest file the server accepts.

## Seams for plugins

All registered by `AddRaindropAttachments(configure?)`, which `AddRaindropServer` calls **before**
`AddRaindropChannels`. Everything is `TryAdd`, so a plugin's earlier registration wins.

| Seam | Default | Use |
|---|---|---|
| `IAttachmentStorage` | `LocalFolderAttachmentStorage` | keep files in object storage, a network share… |
| `IAttachmentPolicy` (enumerable) | `ConfiguredAttachmentPolicy` — the `raindrop.json` rules | add a rule beside it: a per-member quota, a channel that takes only images. Every policy must agree |
| `IAttachmentListener` (enumerable) | none | told when a file is uploaded, sent in a message, or removed. A listener that throws is reported and stepped over |
| `IAttachmentStore` | `RelationalAttachmentStore` via `IDatabaseDriver.CreateAttachmentStore` (refusing default for older drivers) | persistence for the records |
| `IMessageAttachments` (in `Channels/Text/`) | `AttachmentService`; `NoMessageAttachments` when nothing registered one | how text channels hand files to whatever stores them, without depending on it |

Kinds (`file`, `thumbnail`) and states travel as open strings. Thumbnails skip the type rules (they are
whatever image format the client chose) and are capped at 512 KB.

## Reports

A message that carries files is attested under `raindrop/message-attestation/v2`: the v1 fields, then a
4-byte count and, for each file in the message's order, its id (length-prefixed text) and the 32 bytes
of its ciphertext's SHA-256. A report of such a message sends `attachments: [{attachmentId,
ciphertextSha256}]`; the server verifies the v2 signature **and** that those are the files stored with
the message, so a reporter cannot pair the author's words with a different file. See
[Messages](messages.md#reporting-an-encrypted-message). Showing moderators the reported files
themselves, by revealing their keys, is not implemented yet.

## API

All authenticated. `ciphertextSha256` is bytes (base64 in JSON).

| Method | Route | Body → Response |
|---|---|---|
| GET | `/api/attachments/policy` | → `{enabled, maximumFileBytes, maximumFileBytesByExtension, allowedExtensions, blockedExtensions, allowFilesWithoutExtension, maximumFilesPerMessage, maximumThumbnailBytes, previewExtensions, previewAutoLoadMaximumBytes, uploadChunkBytes}` |
| POST | `/api/attachments/uploads/create` | `{channelId, extension, ciphertextSize, kind?}` → `UploadStatus` |
| PUT | `/api/attachments/uploads/chunk` | raw bytes + `Raindrop-Attachment-Id`, `Raindrop-Upload-Offset` headers → `UploadStatus` |
| POST | `/api/attachments/uploads/status` | `{attachmentId}` → `UploadStatus` |
| POST | `/api/attachments/uploads/complete` | `{attachmentId, ciphertextSha256}` → `UploadStatus` |
| POST | `/api/attachments/uploads/cancel` | `{attachmentId}` → `{attachmentId}` |
| POST | `/api/attachments/download` | `{attachmentId}` (+ optional `Range: bytes=…`) → `application/octet-stream`, `206` with `Content-Range` for a range, `416` for one outside the file |

`UploadStatus` is `{attachmentId, state, ciphertextSize, receivedSize, chunkSize, expiresAtUtc}`.

Error codes: `attachments_disabled`, `device_required`, `missing_permission`, `channel_not_found`,
`invalid_attachment`, `file_type_not_allowed`, `file_too_large`, `too_many_pending_uploads`,
`upload_offset_mismatch`, `chunk_too_large` (413), `upload_incomplete`, `upload_finished`,
`hash_mismatch`, `too_many_attachments`, `attachment_already_sent`, `attachment_not_found`,
`attachment_unavailable` (the record exists but storage lost the file).

The capability `attachments.v1` is advertised by `GET /api/server/info` while `attachments.enabled` is on.

## Configuration

See [Configuration](configuration.md#attachments). The console's `attachments` command shows how many
files are stored and how much room they take.

## Tests

`RaindropServer.Tests/AttachmentTests.cs`: the length formula against the Rust core's numbers; every
configuration rule, the megabyte conversion and the size map in `raindrop.json`; the store on every
database (claim races, all-or-nothing claims, ordered chunk progress, deletion and expiry); the service
rules end to end over a real folder (permissions, policy refusals, chunk order and size, hash mismatch,
who may download, deletion and orphan removal); `Range` parsing; and the v2 attestation bytes against a
golden vector the client checks too.

## Related

- [Messages](messages.md) · [Configuration](configuration.md) · [Roles & permissions](roles-and-permissions.md)
- [RaindropCrypto: attachments](../../RaindropCrypto/docs/attachments.md) — the format
- [RaindropClient: attachments](../../RaindropClient/docs/attachments.md) — encrypting, uploading, previews
