# Roles & permissions

Domain code: [`RaindropServer/Roles/`](../RaindropServer/Roles/). Wire surface: `raindrop.v1.RoleService`
([`roles.proto`](../../RaindropProto/raindrop/v1/roles.proto)), served by
[`RoleGrpcService`](../RaindropServer.Grpc/Services/CommunityServices.cs).

**A role is a named set of permissions, a member holds any number of them, and what they may do is the
union.** Roles live on the server and are keyed to the member's UUID, matched at login like everything
else about them. A client is *told* what it may do (so it can grey out a button rather than let a
request fail) but is never *trusted* about it — every check runs server-side, through
`IPermissionResolver`.

## Permissions

Roles hold permissions; **members get roles only, never permissions directly**, and what a member may
do is the union of their roles. Roles are custom and editable, per server — by a member with
`manage_roles` through the role API, or by the owner from the [dashboard](dashboard.md#roles).

The permissions themselves are **hard-coded per build** and modular: each server's build advertises
the ones it knows (`ListPermissions`, with a title and a category for an editor), so a client talking
to an older and a newer server sees each server's own set.

[`Permission`](../RaindropServer/Roles/Permission.cs) is a `[Flags]` enum backed by `long` rather than
the default `int`, leaving room for another fifty-odd permissions. In the database they are stored by
name, not as that number (see [Storage](#storage)). The permissions today:

| Flag | Wire name | Grants |
|---|---|---|
| `ViewChannel` | `view_channel` | See channels and read what's in them |
| `SendMessages` | `send_messages` | Post messages in channels |
| `AttachFiles` | `attach_files` | Add files to the messages they post (checked in the channel alongside `send_messages`; see [Attachments](attachments.md)) |
| `Connect` | `connect` | Join voice channels and hear the people in them |
| `Speak` | `speak` | Talk in voice channels (without it, a member joins as a listener) |
| `Video` | `video` | Turn their camera on in voice channels. Separate from `speak`, so a member can be heard without being seen; the media server refuses their camera without it |
| `ScreenShare` | `screen_share` | Share a screen or a window in voice channels. Separate from `video`; the media server refuses their share without it, and a window's sound additionally needs them to be able to speak |
| `ManageMessages` | `manage_messages` | Delete other members' messages |
| `MuteMembers` | `mute_members` | Mute other members in voice channels, for everyone — checked server-wide, never per channel |
| `CreateChannels` | `create_channels` | Create new channels on this server |
| `RenameChannels` | `rename_channels` | Rename existing channels |
| `DeleteChannels` | `delete_channels` | Delete channels, and everything inside them. Also what makes somebody a manager of a channel's end-to-end encrypted group: reset it without a report, read who reset it and whose commit broke it, and ban a device or account from it ([E2EE](e2ee.md#recovering-a-broken-group)) |
| `ManageRoles` | `manage_roles` | Create roles and hand them out to members |
| `Administrator` | `administrator` | Every permission, including ones added later |

Two conventions hold everywhere in the codebase:

- **Permissions travel as names, never as a bitfield.** A client too old to know a permission lists
  the ones it understands and ignores the rest — exactly like `PresenceStatus` and `ChannelType`.
  [`PermissionNames`](../RaindropServer/Roles/Permission.cs) is the single table everything else is
  derived from (wire names, titles, categories, the parser, and the catalogue
  `RoleService.ListPermissions` serves), so adding a permission means adding one row to it. A row has a
  `Title` ("Manage roles") and a `Category` (`general`, `text`, `voice`, `channels`, `moderation` or
  `administration`, an open set a client files as "other" when it does not know it); `Lookup` turns a
  name into a flag and `Split` separates a stored set into known flags and unknown names.
- **`Administrator` is expanded, never compared literally.** `PermissionExtensions.Expand` turns it
  into every permission this build has, so an administrator is never refused something merely because
  the bit for it wasn't set when their role was written. That's what keeps the built-in owner role
  correct as the enum grows over time.

## Storage

[`RelationalRoleStore`](../RaindropServer/Data/Relational/RelationalRoleStore.cs) stores a role's permissions **by name**, one
row per permission in `raindrop_role_permissions(role_id, permission)`. A name this build does not know
(a role written by a newer build) becomes `RoleRecord.UnknownPermissions`: it grants nothing, is never
sent to a client, and is written back on every save so editing the role here cannot erase it.

`raindrop_roles.permissions` (a `bigint`) is still written with the bits this build knows, so a
downgraded older build keeps working. A role with **no** name rows is read from that column, which is
how roles that predate the table migrate: lazily, and written as names the next time they are saved —
there is no one-time copy. Known limitation: edits made while an older build was running are not
carried back into the name rows after upgrading again, since only a role with no name rows falls back
to the `bigint`.

## The two built-in roles

Seeded by `RoleService.EnsureBuiltInRolesAsync`, called by `InitializeRaindropDatabaseAsync` alongside
the schema — before `ServerHost.Start`, not as a hosted service, because a hosted service has no
guaranteed ordering against Kestrel and the very first request to a fresh server is somebody
registering and expecting to own it.

- **`everyone`** — what an account can do here before anybody has granted it anything. Nobody is
  *assigned* it; holding it is what being an active member means, so the resolver folds it in for
  everyone. It's refused as a target for `assign`/`unassign`. Its permissions **are** editable (that's
  how an owner closes a server down to read-only); its name is not, and it cannot be deleted. It's
  seeded from `RoleOptions.DefaultMemberPermissions` (view, post, attach files, create channels,
  connect, speak and video by default) and *only* seeded — changing that config setting later doesn't reach back and overwrite
  what administrators have since made it. A permission a later build introduces for everybody reaches
  an already-seeded `everyone` role exactly once instead, through a **permission rollout** — see below.
- **`owner`** — holds `Administrator`, and is neither editable nor deletable. A server whose
  administrative role could be stripped of its permissions would be a server nobody could administer
  again.

**The first UUID to register is granted the owner role**, by
[`OwnerClaim`](../RaindropServer/Roles/OwnerClaim.cs), an `IRegistrationListener` that `UserService`
calls after the account row is written. That interface exists so `Users/` never has to learn what a
role is; a listener that throws is reported and stepped over, because the account is already written
and refusing the session at that point would lock the client out of a server it just joined. Ownership
is claimed only while nobody holds it, so the second member to register is an ordinary one. This can
be turned off with `RoleOptions.FirstMemberBecomesOwner = false` for a server whose owner is granted
the role from the console instead (see [Configuration](configuration.md#the-owner-console)).

## Who may change what

Every write goes through `RoleService`, and two rules do the work:

1. **The built-in roles are protected**, per above.
2. **Nobody may hand out what they do not hold.** Creating, editing, deleting, assigning or
   unassigning a role is refused unless the caller already holds every permission involved — for an
   edit, that means both the set the role *already* grants and the set it's being given, or editing
   would be escalation by another route. So `ManageRoles` lets somebody delegate their own authority
   and no more; it is not a path to `Administrator`. Holding `Administrator` satisfies the rule
   trivially, which is the point of it.

One more guard: **the last member holding the owner role keeps it.** There is no account above them to
put a server with no owner right again. Ownership is transferred by granting the role to somebody else
first (then, optionally, revoking it from the original owner). The same rule stops the only owner
*erasing* their account when they leave: [`OwnerLeaveGuard`](../RaindropServer/Roles/OwnerLeaveGuard.cs),
an `ILeaveGuard`, refuses it with `last_owner`. Leaving with the data preserved is allowed, since the
owner can rejoin, by UUID or by username, and keeps the role.

## Permission rollouts

[`PermissionRollouts.All`](../RaindropServer/Roles/PermissionRollouts.cs) lists what newer builds hand
to `everyone` on servers seeded before those permissions existed — today
`new("voice", Connect | Speak)`, `new("attachments", AttachFiles)`, `new("voice-video", Video)` and
`new("voice-screen-share", ScreenShare)`. `EnsureBuiltInRolesAsync` grants each rollout not yet recorded in
`raindrop_permission_rollouts`, then records it: grant first, record second, so a crash between the
two re-applies an OR that changes nothing. Each one is offered **exactly once**, so an owner who takes
the permissions away afterwards keeps them away. A fresh server records every rollout without granting
anything, since its seed already reflects this build.

The list lives in code rather than configuration because the shipped `raindrop.json` names the default
permissions explicitly — an owner's older copy of it must not decide whether members ever receive a
permission it predates. `IRoleStore.ListAppliedRolloutsAsync` / `MarkRolloutAppliedAsync` have default
implementations that throw `NotSupportedException`, so a third-party store written before rollouts still
compiles; seeding then carries on without them.

## Resolving permissions

[`RolePermissionResolver`](../RaindropServer/Roles/RolePermissionResolver.cs) is the only
implementation of `IPermissionResolver`, and every permission check in the server goes through its one
method. It re-reads membership from the user store first — a session outlives the account behind it,
so a member who left in one session cannot keep acting through a token still held in another. That
same check is what makes it safe to keep role assignments after somebody leaves: they grant nothing
until the account is active again, and are still there if that person rejoins.

Roles are **server-wide**, and they are the only source of permissions: creating a channel grants its
creator nothing, so rename, delete and `manage_messages` need a role like everything else. (An earlier
build gave a channel's creator those rights, behind `RoleOptions.ChannelCreatorModeratesOwnChannels`;
both are gone, and the `roles.channelCreatorModeratesOwnChannels` setting is still read but only
produces a warning.) The resolver's `ChannelRecord?` parameter stays as the place per-channel role
overrides would go, if ever wanted. Authors deleting their *own* messages is ownership, not a
permission, and does not go through the resolver. `MuteMembers` is checked server-wide, because a mute
for everyone follows its target into every call.

Drawing a channel list asks the resolver once per channel, so the union of a member's roles is cached
in [`RolePermissionCache`](../RaindropServer/Roles/RolePermissionCache.cs), and **every write through
`RoleService` clears the whole cache.** The invalidation is blunt on purpose: role changes are rare, a
stale permission is a security bug, and a cold cache costs one extra query. It's correct because a
Raindrop server is a single process — the same assumption `InMemorySessionStore` makes. A role granted
directly in SQL (bypassing the running server) takes effect only on the next restart, which is exactly
why the console `grant`/`revoke` commands go through the running server's own `RoleService` rather than
touching the database directly.

## Console and dashboard

`RoleService` has a core method per operation that takes the caller's permissions (`Permission? caller`);
the gRPC methods pass the caller's, and the owner's console and [dashboard](dashboard.md#roles) use the
`CreateFromConsoleAsync`, `UpdateFromConsoleAsync`, `DeleteFromConsoleAsync`, `AssignFromConsoleAsync`,
`UnassignFromConsoleAsync` and `GetForConsoleAsync` twins. They skip the permission check (the owner is
trusted) but not the rest: the built-in role rules, unique names and the last-owner guard still apply,
and the same events are published. Console `grant`/`revoke` are unchanged.

## Role API

All authenticated. Reading is open to any member (a role list isn't a secret, and a client needs it to
show who moderates what). Writing needs `manage_roles` **and** every permission involved.

`raindrop.v1.RoleService`:

| RPC | Purpose |
|---|---|
| `ListRoles` | Every role on this server, highest position first |
| `GetMyRoles` | The caller's own roles, and what they may do server-wide |
| `ListPermissions` | The permissions this build understands, for a role editor |
| `GetRole` | One role, and the UUIDs holding it |
| `CreateRole` | Create a role |
| `UpdateRole` | Rename it, replace its permissions, or move it |
| `DeleteRole` | Delete it and take it away from everyone holding it |
| `AssignRole` | Give a member a role |
| `UnassignRole` | Take a role away from a member |
| `GetMemberRoles` | Which roles one member holds, and what they add up to |

### Domain contracts

Defined in [`RaindropServer/Roles/RoleContracts.cs`](../RaindropServer/Roles/RoleContracts.cs); on the
wire a role is `raindrop.v1.Role`, its kind the enum `RoleKind`, and permissions stay `repeated string`:

- `RoleSummary(RoleId, Name, Kind, Position, Permissions, CreatedAtUtc)` — `Kind` is `custom`,
  `everyone` or `owner`.
- `CreateRoleRequest(Name, Permissions?, Position?)`
- `UpdateRoleRequest(RoleId, Name?, Permissions?, Position?)` — **every field but `RoleId` is optional
  and means "leave this alone" when omitted**, so renaming a role can't accidentally clear what it
  grants. An **empty** permission list *does* clear them — that's the difference between absent and
  empty. On the wire `name` and `position` are `optional` and `permissions` is a `PermissionSet`
  wrapper, so absent and empty stay different.
- `RoleRequest(RoleId)`, `MemberRolesRequest(UserId)`, `MemberRoleRequest(UserId, RoleId)`
- `RoleListResponse(Roles)`
- `RoleDetail(Role, Members)` — the UUIDs holding a role, in grant order; empty for `everyone`, which
  nobody is explicitly assigned.
- `MemberRolesResponse(UserId, Roles, Permissions)` — `Roles` includes `everyone` even though
  nobody is assigned it (highest position first, so it comes last); `Permissions` is the fully-expanded union.
- `RoleDeletedResponse(RoleId, Message)`
- `MemberRoleChangedResponse(UserId, RoleId, Changed, Message)` — `AssignRole`/`UnassignRole` answer with
  `Changed`, which is `false` when the member already held the role or already didn't. **Neither case
  is an error** — the request describes a target state, and that state now holds.
- `PermissionCatalogueResponse(Permissions)` — the catalogue a client builds a role editor from; each
  `PermissionDescription(Name, Title, Category, Summary)`.

### Error codes

On top of the [user error codes](users.md#error-codes): `invalid_role_name`, `role_name_taken`,
`role_not_found`, `invalid_permission`, `role_immutable`, `role_not_assignable`, `last_owner`,
`member_not_found`, `missing_permission`.

## Related

- [Channels](channels.md#permissions) — what permission each channel operation needs.
- [Events](events.md) — `role.created`, `role.updated`, `role.deleted` and `member.roles` are pushed to
  every member, from the API and the console alike. They are the cue for a client to re-read its channel
  list and `GetMyRoles`, since what it may see and do may have changed.
- [Configuration](configuration.md#roles) — the `roles` section of `raindrop.json`.
