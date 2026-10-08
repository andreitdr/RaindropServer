# Attachments

Members can add files of any type to text-channel messages. Code: `RaindropServer/Attachments/` (the
domain), [`AttachmentGrpcService`](../RaindropServer.Grpc/Services/AttachmentGrpcService.cs) (the wire
surface, `raindrop.v1.AttachmentService` in [`attachments.proto`](../../RaindropProto/raindrop/v1/attachments.proto)),
`Data/Relational/RelationalAttachmentStore.cs`.

## The model in one paragraph

Files are **end-to-end encrypted like the messages that carry them**. The sender's client encrypts each
file with a fresh random key in the `raindrop/attachment/v1` format (defined by the Rust core, see
[RaindropCrypto's attachments doc](../../RaindropCrypto/docs/attachments.md)), uploads the ciphertext in
a stream of frames, and then posts an ordinary encrypted message (`SendEncryptedMessage`) whose MLS payload holds the
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
| `preview.*` | nothing; served to clients through `GetAttachmentPolicy` | a hint for clients, which render previews themselves (the server cannot make a thumbnail of a file it cannot read, so the **sender** uploads one as its own `thumbnail` attachment) |

A blocklist is a courtesy rather than a defence (any file can be renamed), which is why clients never
open a received file by themselves either.

## Lifecycle

```text
CreateUpload ──► uploading ──Upload stream(s)──► CompleteUpload ──► uploaded ──SendEncryptedMessage──► attached
                      │                                               │                               │
                      └──────── CancelUpload, or pendingUploadExpiryHours ──► deleted ◄── message or channel deleted
                                                                                    │
                                                                 AttachmentMaintenanceService removes the file
```

1. **Create** (`CreateUpload`) — resolves the channel (404 when hidden, as for
   messages), requires `send_messages` **and** `attach_files` there and a **bound session**
   (`device_required`), checks the declared extension and size against every `IAttachmentPolicy`, and
   reserves an id. Answers with an `UploadStatus`.
2. **Upload** (the client-streaming `Upload`) — an `UploadHeader {attachment_id, offset}` first, then
   data frames of at most `upload_frame_bytes` (256 KiB, `AttachmentGrpcService.FrameBytes`; a bigger one
   is `chunk_too_large`). The offset must be exactly what the server has received
   (`upload_offset_mismatch` otherwise). The service writes the frames as a run of **chunks** of at most
   `uploadChunkBytes` (8 MiB) through `AttachmentService.WriteChunkAsync`, each committed as it
   completes, so the database is touched once per chunk rather than once per frame. Nothing may run
   past the declared size (`chunk_too_large`). A stream that breaks keeps every committed chunk; the part
   of a chunk that was in flight is written again by the next stream, from the committed offset, and
   storage truncates to it. Writes to one upload are serialised in-process.
3. **Resume** (`GetUploadStatus`) — how many bytes have arrived: where the next `Upload` starts.
4. **Complete** (`CompleteUpload`) — every byte must be there
   (`upload_incomplete`) and hash to the SHA-256 the uploader computed; a mismatch throws the upload
   away (`hash_mismatch`). The file is then committed in storage and becomes `uploaded`. Completing
   twice is not an error.
5. **Send** — `SendEncryptedMessage` with `attachment_ids`. The files are
   claimed for the new message **before** it is stored, atomically and all-or-nothing (a conditional
   `UPDATE … WHERE state = 'uploaded'` per file in one transaction), so two messages can never carry
   one file; if storing the message fails, they are released again. Every file must be the caller's,
   finished, uploaded for this channel, and not already sent. At most `maximumFilesPerMessage` files,
   plus at most one thumbnail per file. `attach_files` is checked again here, since it may have been
   taken away since the upload. Plaintext `SendMessage` cannot carry files.
6. **Read** — `ChannelMessage.attachments` lists `MessageAttachment {attachment_id, kind, extension,
   ciphertext_size, ciphertext_sha256}` in history and in `message_created`; the server-streaming
   `Download` sends the ciphertext to anybody who can see the channel (the uploader alone, before it is
   sent): a `DownloadHeader {ciphertext_size, offset}`, then data frames, from the requested `offset` — how
   a client resumes, appending to what it already has (`invalid_offset` past the end). Everything
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

## Attachment API

All authenticated. `raindrop.v1.AttachmentService`:

| RPC | Request → Response |
|---|---|
| `GetAttachmentPolicy` | → `AttachmentPolicy {enabled, maximum_file_bytes, maximum_file_bytes_by_extension, allowed_extensions?, blocked_extensions, allow_files_without_extension, maximum_files_per_message, maximum_thumbnail_bytes, preview_extensions, preview_auto_load_maximum_bytes, upload_frame_bytes}` — `allowed_extensions` absent means any extension that is not blocked |
| `CreateUpload` | `{channel_id, extension, ciphertext_size, kind}` → `UploadStatus` (`ATTACHMENT_KIND_UNSPECIFIED` means a file) |
| `Upload` *(client stream)* | `UploadHeader {attachment_id, offset}`, then `data` frames → `UploadStatus` |
| `GetUploadStatus` | `{attachment_id}` → `UploadStatus` |
| `CompleteUpload` | `{attachment_id, ciphertext_sha256}` → `UploadStatus` |
| `CancelUpload` | `{attachment_id}` → `{attachment_id}` |
| `Download` *(server stream)* | `{attachment_id, offset}` → `DownloadHeader {ciphertext_size, offset}`, then `data` frames |

`UploadStatus` is `{attachment_id, state, ciphertext_size, received_size, frame_bytes, expires_at}`, its
state the enum `UploadState` (`UPLOADING`, `UPLOADED`, `ATTACHED`). See RaindropProto's
[streams doc](../../RaindropProto/docs/streams.md) for both streams.

Error codes: `attachments_disabled`, `device_required`, `missing_permission`, `channel_not_found`,
`invalid_attachment`, `file_type_not_allowed`, `file_too_large`, `too_many_pending_uploads`,
`upload_offset_mismatch`, `chunk_too_large`, `invalid_frame` (an upload stream that does not start with
its header, or has a second one), `upload_incomplete`, `upload_finished`, `hash_mismatch`,
`too_many_attachments`, `attachment_already_sent`, `attachment_not_found`, `invalid_offset`,
`attachment_unavailable` (the record exists but storage lost the file).

The capability `attachments.v1` is advertised by `GetServerInfo` while `attachments.enabled` is on.

## Configuration

See [Configuration](configuration.md#attachments). The console's `attachments` command shows how many
files are stored and how much room they take.

## Tests

`RaindropServer.Tests/AttachmentTests.cs`: the length formula against the Rust core's numbers; every
configuration rule, the megabyte conversion and the size map in `raindrop.json`; the store on every
database (claim races, all-or-nothing claims, ordered chunk progress, deletion and expiry); the service
rules end to end over a real folder (permissions, policy refusals, chunk order and size, hash mismatch,
who may download, deletion and orphan removal); and the v2 attestation bytes against a golden vector the
client checks too. `GrpcTests.cs` covers the streams over a real gRPC client: an upload in frames that
straddle chunks, resumed after an offset mismatch, completed, and downloaded from an offset; an oversized
frame; and a download of an id the caller cannot reach.

## Related

- [Messages](messages.md) · [Configuration](configuration.md) · [Roles & permissions](roles-and-permissions.md)
- [RaindropCrypto: attachments](../../RaindropCrypto/docs/attachments.md) — the format
- [RaindropClient: attachments](../../RaindropClient/docs/attachments.md) — encrypting, uploading, previews
