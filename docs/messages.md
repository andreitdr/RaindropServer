# Messages

Domain code: [`RaindropServer/Channels/Text/`](../RaindropServer/Channels/Text/). HTTP surface:
[`ServerCli/Endpoints/Channels/Text/`](../ServerCli/Endpoints/Channels/Text/). Depends on
[Channels](channels.md) for the channel itself and [Roles & permissions](roles-and-permissions.md) for
every access check.

A message belongs to a channel and is tied to the author's **UUID** — never to their account, which
may be erased, and never to their username, which may change. `POST /api/users/lookup` (see
[Users](users.md)) is how a client turns that UUID into a display name.

## End-to-end encryption

**Messages are end-to-end encrypted.** A member's device encrypts each message for the channel's MLS
group (see [E2EE](e2ee.md)) and posts the ciphertext to `send-encrypted`. The server stores and relays
it without being able to read it. What it does check is what it can see:

- the message is an MLS application message (`PrivateMessage`) for **this** channel's group
- it was sent in the group's current epoch or one of the last `MaximumPastEpochs` (2); otherwise
  `409 stale_epoch`, and the client catches up and encrypts again
- it comes from a session **bound to one of the author's devices** (`device_required`)
- it is no more than `MaximumEncryptedMessageBytes` (64 KiB) of ciphertext
- the author has `SendMessages`, as for any post

An encrypted `ChannelMessage` has no `Content`. Instead it carries `Ciphertext`, `Epoch` and
`SenderDeviceId`, and those three are null on a plaintext message.

- **Files.** `send-encrypted` takes an optional `AttachmentIds`: finished uploads of the caller's for
  this channel, claimed for the message before it is stored (see [Attachments](attachments.md)). The
  message then carries `Attachments` — `{attachmentId, kind, extension, ciphertextSize,
  ciphertextSha256}` each — in history and in `message.created`; null for a message without files and
  for a deleted one. The files' names, types and keys are inside the ciphertext. Plaintext `send`
  cannot carry files.

- **Plaintext posting is off by default** (`channels.allowPlaintextMessages`, see
  [Configuration](configuration.md#channels)). The plain `send` answers `400 encryption_required`
  unless an owner turns it back on for clients that cannot encrypt.
- **Messages from before the switch stay plaintext.** Clients mark them as not end-to-end encrypted.
  The owner console's `purge-plaintext` (it asks for `yes`) deletes every one of them and the whole
  deleted-message archive: everything the server could read.
- **Storage.** Encrypted messages live in `raindrop_encrypted_messages`, a table of their own, so
  existing databases gain it without an `ALTER`. `RelationalTextMessageStore` routes each record by
  `TextMessageRecord.IsEncrypted` and merges both tables when reading history. Both are ordered by
  the same `(sent_at, message_id)` cursor, so paging stays exact across old plaintext and new
  ciphertext.
- **Deletion** works the same, as a tombstone: the ciphertext is nulled, `deleted_at`/`deleted_by`
  are set, and the row stays.
- **The archive.** Nothing is archived for an encrypted message: the server never had its text, and
  `ArchivingMessageDeletionListener` skips a message with no `Content`.
- **Forward-only history.** A device can decrypt only what was sent after it joined the channel's
  group, and keeps the plaintext in its own encrypted store. Nothing, the server included, can give a
  new device the keys to older messages.

### Reporting an encrypted message

The server cannot read an encrypted message, so a report has to bring the text with it, in a way that
proves who wrote it. `MessageReportService` does this.

- **The attestation.** When a device sends a message, it signs an *attestation* with its Ed25519
  device key and puts it inside the encrypted payload (`a`). The signature covers
  `"raindrop/message-attestation/v1\n"` followed by the channel id (lower case), the client message
  id, the client's send time in ms, the type and the body. Each text field is a 4-byte big-endian
  length and its UTF-8.
- **Reporting.** A member who reports the message (`POST .../report`) reveals those fields and the
  attestation. The server recomputes the bytes and verifies the signature against the key of the
  device the stored message came from (`SenderDeviceId`, in `raindrop_devices`; a revoked device
  still counts, since it held that key when it wrote). A reporter can therefore reveal a message, but
  cannot invent one or change its words (`invalid_attestation`).
- **Where reports go.** Accepted reports are stored in `raindrop_message_reports`, the only place the
  server holds encrypted-message text, and only what a member chose to reveal. They are sent as
  `message.reported` to members with `manage_messages` in the channel, who can list them with
  `POST .../reports`. The owner console's `reports` command shows the latest 20.
- **What it proves** is authorship: that device wrote those words in that channel. It does not bind
  them to the exact stored ciphertext, which only group members can decrypt. A reporter could present
  a different message the same device genuinely sent in the same channel.
- **Messages with files** are attested under `"raindrop/message-attestation/v2\n"`: the v1 fields,
  then a 4-byte big-endian count of files and, for each in the message's order, its id (lower case, as
  a length-prefixed field) and the 32 bytes of its ciphertext's SHA-256. The report carries
  `attachments: [{attachmentId, ciphertextSha256}]`, and the server checks both the signature and that
  those are the files stored with **this** message, so words cannot be paired with another file.

## Deletion is a tombstone

**Deleting a message keeps the row.** The text is nulled and `deleted_at`/`deleted_by` are set, so the
message stays in place, rendered by the client as "message deleted." This mirrors how an erased user
account works (see [Users](users.md)): the tie between a message and a UUID is never something anyone
can remove. Keeping the row also keeps history paging honest — the row a client's cursor points at can
never vanish out from under it mid-page.

Deleting a **channel**, by contrast, does remove its messages — the owning `IChannelModule` does that
just before the channel row itself is deleted (see [Channels](channels.md#ichannelmodule)).

### Who may delete what

Two independent rules:

- **Anyone may always delete their own message.** No permission is involved — not `ManageMessages`,
  not `SendMessages` — so a member whose roles grant almost nothing can still take back what they said.
  (They must still be able to *see* the channel: one they cannot see answers `channel_not_found`.)
- Deleting **someone else's** message needs `ManageMessages` in that channel (a channel's creator has
  it implicitly, see [Channels](channels.md#permissions)).

## The deleted-message archive

Nulling the text destroys it, so before it goes, the original is **copied into the database** —
`raindrop_deleted_messages` — with who wrote it, who deleted it, when, and why. This happens for a single
deleted message *and* for every message in a channel that is deleted (`reason` is `message_deleted` or
`channel_deleted`; it is an open string, so a plugin may add its own).

What the archive is, and is not:

- **It is for the server owner, in the database, and nowhere else.** No endpoint reads it, no
  permission grants it, no event carries it and no client is ever sent it. A deleted message stays
  invisible in every client, exactly as before; the owner reads the table directly.
- **It is not erasure.** Deleting a message hides it from everybody but the owner. It also outlives its
  author's account — an [erased user](users.md) leaves their archived messages behind, tied to the same
  UUID the live ones are — and there is no retention sweeper on it (yet). Clients are expected to say so
  when they ask somebody to confirm a delete.
- **A delete fails rather than lose the copy.** The copy is written first, and if that fails the delete
  is refused with nothing changed and nothing published. A failure part-way can leave a preserved copy of a
  message that is still there, never a deleted message with no copy — and retrying the delete does not
  duplicate it (the message id is the table's key, and the insert skips what is already there).
- **Only text that still exists is copied.** Messages that were deleted before this existed lost their
  text and cannot be recovered; and when a channel is deleted its already-deleted messages are skipped,
  their text having been preserved (or lost) when they were deleted.

### `IMessageDeletionListener` — the seam

The copy is made by the default listener, `ArchivingMessageDeletionListener`, behind a public interface
([`IMessageDeletionListener`](../RaindropServer/Channels/Text/IMessageDeletionListener.cs)) that a plugin can
add another implementation of — to forward deleted messages elsewhere, alert a moderator, and so on.
Register it with `TryAddEnumerable`, so it runs *beside* the default rather than replacing it:

```csharp
public interface IMessageDeletionListener
{
    Task OnMessageDeletingAsync(MessageDeletion deletion, CancellationToken ct);          // one message, content still intact
    Task OnChannelMessagesDeletingAsync(ChannelDeletion deletion, CancellationToken ct); // a whole channel, before its messages go
}
```

Both have a do-nothing default, so a listener implements only what it wants. They run **before** the
text is destroyed, and — unlike [`IRegistrationListener`](users.md) — a listener that throws **stops the
deletion**. That is deliberate (see above), and it means listeners must be idempotent: a failed delete is
retried from the start. The channel hook is called by `TextChannelModule`, not by the shared
`ChannelService`, so the shared channel code still never learns what a message is.

The default archive itself sits behind [`IDeletedMessageStore`](../RaindropServer/Channels/Text/IDeletedMessageStore.cs),
one implementation per database module like every other store.

## History paging

History is paged **backwards, newest page first**, through an opaque cursor
([`MessageCursor.cs`](../RaindropServer/Channels/Text/MessageCursor.cs)). The cursor is a
`(sent_at, message_id)` pair rather than a bare timestamp, because two messages can share a timestamp
and the id is what breaks the tie — so a page boundary always falls between two specific rows, and
paging can neither repeat nor skip a message.

Each page itself arrives in **chronological order** (the order it should be rendered in);
`nextCursor` leads further into the past, and comes back `null` at the beginning of the channel.

## Message API

All authenticated, all under `/api/channels/text/messages`.

| Method | Route | Purpose |
|---|---|---|
| POST | `/api/channels/text/messages/send-encrypted` | Post an end-to-end encrypted message (`{channelId, message, attachmentIds?}`) — needs `SendMessages` and a bound session, and `AttachFiles` to carry files |
| POST | `/api/channels/text/messages/send` | Post a plaintext message — refused with `encryption_required` unless `allowPlaintextMessages` |
| POST | `/api/channels/text/messages/history` | One page of history, newest page first; follow `nextCursor` |
| POST | `/api/channels/text/messages/delete` | Delete a message: your own always, anyone else's with `ManageMessages` |
| POST | `/api/channels/text/messages/report` | Report an encrypted message: `{messageId, body, clientMessageId, clientSentAtUnixMs, type, attestation, reason?, attachments?}` |
| POST | `/api/channels/text/messages/reports` | A channel's reports, newest first — needs `ManageMessages` there |

Endpoint implementations:
[`ServerCli/Endpoints/Channels/Text/`](../ServerCli/Endpoints/Channels/Text/)
(`SendEncryptedMessageEndpoint.cs`, `SendMessageEndpoint.cs`, `MessageHistoryEndpoint.cs`,
`DeleteMessageEndpoint.cs`).

### Request/response shapes

Defined in
[`RaindropServer/Channels/Text/TextChannelContracts.cs`](../RaindropServer/Channels/Text/TextChannelContracts.cs):

- `ChannelMessage(MessageId, ChannelId, AuthorId, Content?, SentAtUtc, Deleted, DeletedAtUtc?, DeletedBy?,
  Ciphertext?, Epoch?, SenderDeviceId?, Attachments?)` — `Content` is null once deleted, and always for
  an encrypted message, which carries `Ciphertext` (null once deleted), `Epoch` and `SenderDeviceId`;
  `Deleted` is the flag a client renders the tombstone from. `Attachments` is a list of
  `MessageAttachment(AttachmentId, Kind, Extension, CiphertextSize, CiphertextSha256?)`.
- `SendEncryptedMessageRequest(ChannelId, Message, AttachmentIds?)` — `Message` is the MLS ciphertext;
  `AttachmentIds` the finished uploads it carries (see [Attachments](attachments.md)).
- `SendMessageRequest(ChannelId, Content)`
- `MessageHistoryRequest(ChannelId, Before?, Limit?)` — `Before` is the previous page's `NextCursor`,
  or omitted for the newest messages; `Limit` defaults to the server's configured page size and is
  capped at its configured maximum (see [Configuration](configuration.md#channels)).
- `DeleteMessageRequest(MessageId)`
- `MessageHistoryResponse(ChannelId, Messages, NextCursor?)`

### Error codes

On top of the [channel error codes](channels.md#error-codes): `invalid_message`,
`message_too_long`, `message_not_found`, `invalid_cursor`, and for encryption `encryption_required`,
`device_required`, `invalid_mls_message`, `wrong_group`, `group_not_found`, `stale_epoch`, and for
reports `not_encrypted`, `invalid_attestation`, `invalid_reason`. A message carrying files can also
be refused with the [attachment error codes](attachments.md#api) (`attachments_disabled`,
`too_many_attachments`, `attachment_not_found`, `attachment_already_sent`, `upload_incomplete`,
`invalid_attachment`).

## Related

- [Channels](channels.md) — the container messages live in, and where `IChannelModule` deletes them.
- [Database modules](database-modules.md) — the `raindrop_deleted_messages` table.
- [Events](events.md) — `message.created` and `message.deleted` (the tombstone) are pushed to every
  member who can see the channel, so a connected client need not poll history.
- The client's [messages & chat doc](../../RaindropClient/docs/messages-chat.md) — following the event
  stream, optimistic send and its echo, and how tombstones are rendered.
