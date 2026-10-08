# Channels

Domain code: [`RaindropServer/Channels/`](../RaindropServer/Channels/), with
[`Channels/Text/`](../RaindropServer/Channels/Text/) and [`Channels/Voice/`](../RaindropServer/Channels/Voice/)
for what's specific to each type. Wire surface: `raindrop.v1.ChannelService`
([`channels.proto`](../../RaindropProto/raindrop/v1/channels.proto)) for the shared lifecycle, with
`TextChannelService` and `VoiceService` for each type, all in
[`RaindropServer.Grpc/Services/`](../RaindropServer.Grpc/Services/CommunityServices.cs).

Channels are how members talk to each other. There are two kinds, and the split runs all the way down
the code:

- **Text channels** — messages in chronological order. **Implemented.**
- **Voice channels** — a group call, later with camera and screen sharing. **Implemented**, with the
  audio carried by a LiveKit sidecar; see [Voice](voice.md). On a server with no media server
  configured they can still be created, but joining one is refused with `voice_unavailable`.

## Shared vs. per-type code

`RaindropServer/Channels/` holds what every channel has in common: the record
([`ChannelRecord.cs`](../RaindropServer/Channels/ChannelRecord.cs)), the name policy
([`ChannelNamePolicy.cs`](../RaindropServer/Channels/ChannelNamePolicy.cs)), the type enum
([`ChannelType.cs`](../RaindropServer/Channels/ChannelType.cs)), and
[`ChannelService`](../RaindropServer/Channels/ChannelService.cs) for the lifecycle: create, list, get,
rename, delete.

`RaindropServer/Channels/Text/` holds everything true only of text channels — see
[Messages](messages.md). `RaindropServer/Channels/Voice/` holds everything true only of voice
channels — see [Voice](voice.md) — and was added without touching `ChannelService`.

### `IChannelModule`

What keeps the shared half from growing a `switch` over channel type is
[`IChannelModule`](../RaindropServer/Channels/IChannelModule.cs): one small registration per kind of
channel, saying which `ChannelType` it implements and how to delete what a channel of that type holds.

`ChannelService` refuses to create a type that no module claims — the single line of defence that
keeps a channel type from appearing before the code that runs it exists — and calls the owning
module when a channel is deleted, so the shared service never has to learn what a "message" even is.
Adding a new channel type means adding a module and its own service, not editing `ChannelService`.

The module is handed the whole [`ChannelDeletion`](../RaindropServer/Channels/ChannelDeletion.cs) — the
channel record, who is deleting it and when — rather than just an id, so it can preserve what it is about to
destroy while the channel's name still exists to describe it. `TextChannelModule` uses that to run the
[`IMessageDeletionListener`](messages.md#imessagedeletionlistener--the-seam)s (which is how the messages in
a deleted channel end up in the [deleted-message archive](messages.md#the-deleted-message-archive)) and
only then delete them.

Registered modules today (see [gRPC services & DI wiring](grpc-services.md#addraindropchannelsconfigure)):
`TextChannelModule`, and `VoiceChannelModule` (registered by `AddRaindropVoice`, whether or not a media
server is configured, so voice channels can always be deleted). Deleting a voice channel ends its call,
best-effort against the media server — an outage there must not block the delete.

Deliberately, a module depends on stores rather than on its type's service: every module is a
dependency of `ChannelService`, which each type's service depends on in turn. The audience helper a
module needs for its events is `ChannelAudience.For(resolver, channel)`, the static that
`ChannelService.AudienceFor` itself delegates to.

## Naming

Channel names are unique per server, on a lower-cased `name_normalized` column with a unique index —
`#general` means one place to everybody. Same rule, same reasoning, and nearly the same policy class
([`ChannelNamePolicy`](../RaindropServer/Channels/ChannelNamePolicy.cs)) as usernames
(see [Users](users.md)).

## Permissions

Channels own no permission rules of their own: every check goes through the single
`IPermissionResolver` that the roles subsystem registers — see
[Roles & permissions](roles-and-permissions.md#resolving-permissions). What channels contribute is
*which* permission each operation needs, and the fact that `ChannelService.ResolveAsync` asks the
resolver once and hands the answer down to the type's own service:

| Operation | Permission needed |
|---|---|
| Post a message | `SendMessages` |
| Rename a channel | `RenameChannels` (or having created it — see below) |
| Delete a channel | `DeleteChannels` (or having created it) — its messages are archived first |
| Remove somebody else's message | `ManageMessages` (deleting your *own* needs no permission) |

Every channel a client is shown carries `yourPermissions`, so the client can grey out what the caller
cannot do rather than guessing at the server's rules. A channel the caller may not see (no
`ViewChannel`) is reported as `channel_not_found` rather than `403 Forbidden`, so the API cannot be
used to enumerate channels somebody has been kept out of.

## Channel API

All authenticated. `raindrop.v1.ChannelService`:

| RPC | Purpose |
|---|---|
| `ListChannels` | Every channel the caller may see, with `your_permissions` for each |
| `CreateChannel` | Create a channel of a given type: `text` or `voice` |
| `GetChannel` | One channel, and what the caller may do in it |
| `RenameChannel` | Rename it — needs `RenameChannels`, or having created it |
| `DeleteChannel` | Delete it and everything in it — needs `DeleteChannels`, or having created it. The messages are [copied to the archive](messages.md#the-deleted-message-archive) first |

Messages (`TextChannelService`) are documented in [Messages](messages.md), and calls (`VoiceService`)
in [Voice](voice.md).

### Domain contracts

Defined in [`RaindropServer/Channels/ChannelContracts.cs`](../RaindropServer/Channels/ChannelContracts.cs);
on the wire a channel is `raindrop.v1.Channel`, its type an open string:

- `ChannelSummary(ChannelId, Type, Name, CreatedBy, CreatedAtUtc, YourPermissions)` — `Type` is `text`
  or `voice`.
- `CreateChannelRequest(Type, Name)`
- `ChannelRequest(ChannelId)`
- `RenameChannelRequest(ChannelId, Name)`
- `ChannelListResponse(Channels)` — ordered by name.
- `ChannelDeletedResponse(ChannelId, Message)`

### Error codes

On top of the [user error codes](users.md#error-codes): `invalid_channel_type`,
`channel_type_unsupported`, `invalid_channel_name`, `channel_name_taken`, `channel_not_found`,
`wrong_channel_type`, `missing_permission`. Voice adds its own — see [Voice](voice.md#voice-api).

## Related

- [Messages](messages.md) — the text-channel-specific domain and its API.
- [Voice](voice.md) — the voice-channel-specific domain and its API.
- [Events](events.md) — `channel.created`, `channel.renamed` and `channel.deleted`, each sent only to
  members who can see the channel, with their own `yourPermissions`. `ChannelService.AudienceFor` is
  the audience every event about a channel uses, plugins' included.
- [Roles & permissions](roles-and-permissions.md) — where every permission check actually happens.
- The client's [channels feature doc](../../RaindropClient/docs/channels.md).
