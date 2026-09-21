# Messages

Domain code: [`RaindropServer/Channels/Text/`](../RaindropServer/Channels/Text/). HTTP surface:
[`ServerCli/Endpoints/Channels/Text/`](../ServerCli/Endpoints/Channels/Text/). Depends on
[Channels](channels.md) for the channel itself and [Roles & permissions](roles-and-permissions.md) for
every access check.

A message belongs to a channel and is tied to the author's **UUID** — never to their account, which
may be erased, and never to their username, which may change. `POST /api/users/lookup` (see
[Users](users.md)) is how a client turns that UUID into a display name.

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
| POST | `/api/channels/text/messages/send` | Post a message — needs `SendMessages` |
| POST | `/api/channels/text/messages/history` | One page of history, newest page first; follow `nextCursor` |
| POST | `/api/channels/text/messages/delete` | Delete a message: your own always, anyone else's with `ManageMessages` |

Endpoint implementations:
[`ServerCli/Endpoints/Channels/Text/`](../ServerCli/Endpoints/Channels/Text/)
(`SendMessageEndpoint.cs`, `MessageHistoryEndpoint.cs`, `DeleteMessageEndpoint.cs`).

### Request/response shapes

Defined in
[`RaindropServer/Channels/Text/TextChannelContracts.cs`](../RaindropServer/Channels/Text/TextChannelContracts.cs):

- `ChannelMessage(MessageId, ChannelId, AuthorId, Content?, SentAtUtc, Deleted, DeletedAtUtc?, DeletedBy?)`
  — `Content` is null once deleted; `Deleted` is the flag a client renders the tombstone from.
- `SendMessageRequest(ChannelId, Content)`
- `MessageHistoryRequest(ChannelId, Before?, Limit?)` — `Before` is the previous page's `NextCursor`,
  or omitted for the newest messages; `Limit` defaults to the server's configured page size and is
  capped at its configured maximum (see [Configuration](configuration.md#channels)).
- `DeleteMessageRequest(MessageId)`
- `MessageHistoryResponse(ChannelId, Messages, NextCursor?)`

### Error codes

On top of the [channel error codes](channels.md#error-codes): `invalid_message`,
`message_too_long`, `message_not_found`, `invalid_cursor`.

## Related

- [Channels](channels.md) — the container messages live in, and where `IChannelModule` deletes them.
- [Database modules](database-modules.md) — the `raindrop_deleted_messages` table.
- [Events](events.md) — `message.created` and `message.deleted` (the tombstone) are pushed to every
  member who can see the channel, so a connected client need not poll history.
- The client's [messages & chat doc](../../RaindropClient/docs/messages-chat.md) — following the event
  stream, optimistic send and its echo, and how tombstones are rendered.
