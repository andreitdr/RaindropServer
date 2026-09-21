# Roles & permissions

Domain code: [`RaindropServer/Roles/`](../RaindropServer/Roles/). HTTP surface:
[`ServerCli/Endpoints/Roles/`](../ServerCli/Endpoints/Roles/).

**A role is a named set of permissions, a member holds any number of them, and what they may do is the
union.** Roles live on the server and are keyed to the member's UUID, matched at login like everything
else about them. A client is *told* what it may do (so it can grey out a button rather than let a
request fail) but is never *trusted* about it — every check runs server-side, through
`IPermissionResolver`.

## Permissions

[`Permission`](../RaindropServer/Roles/Permission.cs) is a `[Flags]` enum backed by `long` rather than
the default `int`: the permission set lives in a single database column, there's no schema migration
mechanism (see [Database modules](database-modules.md)), so the room for another fifty-odd permissions
has to exist from day one. The permissions today:

| Flag | Wire name | Grants |
|---|---|---|
| `ViewChannel` | `view_channel` | See channels and read what's in them |
| `SendMessages` | `send_messages` | Post messages in channels |
| `Connect` | `connect` | Join voice channels and hear the people in them |
| `Speak` | `speak` | Talk in voice channels (without it, a member joins as a listener) |
| `ManageMessages` | `manage_messages` | Delete other members' messages |
| `MuteMembers` | `mute_members` | Mute other members in voice channels, for everyone — checked server-wide, never per channel |
| `CreateChannels` | `create_channels` | Create new channels on this server |
| `RenameChannels` | `rename_channels` | Rename existing channels |
| `DeleteChannels` | `delete_channels` | Delete channels, and everything inside them |
| `ManageRoles` | `manage_roles` | Create roles and hand them out to members |
| `Administrator` | `administrator` | Every permission, including ones added later |

Two conventions hold everywhere in the codebase:

- **Permissions travel as names, never as a bitfield.** A client too old to know a permission lists
  the ones it understands and ignores the rest — exactly like `PresenceStatus` and `ChannelType`.
  [`PermissionNames`](../RaindropServer/Roles/Permission.cs) is the single table everything else is
  derived from (wire names, the parser, and the catalogue `GET /api/roles/permissions` serves), so
  adding a permission means adding one row to it.
- **`Administrator` is expanded, never compared literally.** `PermissionExtensions.Expand` turns it
  into every permission this build has, so an administrator is never refused something merely because
  the bit for it wasn't set when their role was written. That's what keeps the built-in owner role
  correct as the enum grows over time.

## The two built-in roles

Seeded by `RoleService.EnsureBuiltInRolesAsync`, called by `InitializeRaindropDatabaseAsync` alongside
the schema — before `ServerHost.Start`, not as a hosted service, because a hosted service has no
guaranteed ordering against Kestrel and the very first request to a fresh server is somebody
registering and expecting to own it.

- **`everyone`** — what an account can do here before anybody has granted it anything. Nobody is
  *assigned* it; holding it is what being an active member means, so the resolver folds it in for
  everyone. It's refused as a target for `assign`/`unassign`. Its permissions **are** editable (that's
  how an owner closes a server down to read-only); its name is not, and it cannot be deleted. It's
  seeded from `RoleOptions.DefaultMemberPermissions` (view, post, create channels, connect and speak
  by default) and *only* seeded — changing that config setting later doesn't reach back and overwrite
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
first (then, optionally, revoking it from the original owner).

## Permission rollouts

[`PermissionRollouts.All`](../RaindropServer/Roles/PermissionRollouts.cs) lists what newer builds hand
to `everyone` on servers seeded before those permissions existed — today one entry,
`new("voice", Connect | Speak)`. `EnsureBuiltInRolesAsync` grants each rollout not yet recorded in
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

Roles are **server-wide** — the resolver's optional `ChannelRecord?` parameter only changes the answer
for one rule, which is about a particular channel: **whoever creates a channel moderates it**
(renaming, deleting, and removing other members' messages in it), whatever their roles otherwise say.
`MuteMembers` is deliberately *not* part of that rule: a mute for everyone follows its target into
every call on the server, so it is checked server-wide.
That's the rule the server ran on before it had roles at all, kept so a member who can create a
channel isn't unable to tidy it up afterwards. `RoleOptions.ChannelCreatorModeratesOwnChannels` turns
it off for a server where roles alone should decide. Per-channel role overrides, if ever wanted, would
go here and nowhere else.

Drawing a channel list asks the resolver once per channel, so the union of a member's roles is cached
in [`RolePermissionCache`](../RaindropServer/Roles/RolePermissionCache.cs), and **every write through
`RoleService` clears the whole cache.** The invalidation is blunt on purpose: role changes are rare, a
stale permission is a security bug, and a cold cache costs one extra query. It's correct because a
Raindrop server is a single process — the same assumption `InMemorySessionStore` makes. A role granted
directly in SQL (bypassing the running server) takes effect only on the next restart, which is exactly
why the console `grant`/`revoke` commands go through the running server's own `RoleService` rather than
touching the database directly.

## Role API

All authenticated. Reading is open to any member (a role list isn't a secret, and a client needs it to
show who moderates what). Writing needs `manage_roles` **and** every permission involved.

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/roles` | Every role on this server, highest position first |
| GET | `/api/roles/me` | The caller's own roles, and what they may do server-wide |
| GET | `/api/roles/permissions` | The permissions this build understands, for a role editor |
| POST | `/api/roles/get` | One role, and the UUIDs holding it |
| POST | `/api/roles/create` | Create a role |
| POST | `/api/roles/update` | Rename it, replace its permissions, or move it |
| POST | `/api/roles/delete` | Delete it and take it away from everyone holding it |
| POST | `/api/roles/assign` | Give a member a role |
| POST | `/api/roles/unassign` | Take a role away from a member |
| POST | `/api/roles/member` | Which roles one member holds, and what they add up to |

Endpoint implementations: [`ServerCli/Endpoints/Roles/`](../ServerCli/Endpoints/Roles/)
(`ListRolesEndpoint.cs`, `MyRolesEndpoint.cs`, `PermissionCatalogueEndpoint.cs`, `GetRoleEndpoint.cs`,
`CreateRoleEndpoint.cs`, `UpdateRoleEndpoint.cs`, `DeleteRoleEndpoint.cs`, `AssignRoleEndpoint.cs`,
`UnassignRoleEndpoint.cs`, `MemberRolesEndpoint.cs`).

### Request/response shapes

Defined in [`RaindropServer/Roles/RoleContracts.cs`](../RaindropServer/Roles/RoleContracts.cs):

- `RoleSummary(RoleId, Name, Kind, Position, Permissions, CreatedAtUtc)` — `Kind` is `custom`,
  `everyone` or `owner`.
- `CreateRoleRequest(Name, Permissions?, Position?)`
- `UpdateRoleRequest(RoleId, Name?, Permissions?, Position?)` — **every field but `RoleId` is optional
  and means "leave this alone" when omitted**, so renaming a role can't accidentally clear what it
  grants. An **empty** permission list *does* clear them — that's the difference between absent and
  empty.
- `RoleRequest(RoleId)`, `MemberRolesRequest(UserId)`, `MemberRoleRequest(UserId, RoleId)`
- `RoleListResponse(Roles)`
- `RoleDetail(Role, Members)` — the UUIDs holding a role, in grant order; empty for `everyone`, which
  nobody is explicitly assigned.
- `MemberRolesResponse(UserId, Roles, Permissions)` — `Roles` includes `everyone` first even though
  nobody is assigned it; `Permissions` is the fully-expanded union.
- `RoleDeletedResponse(RoleId, Message)`
- `MemberRoleChangedResponse(UserId, RoleId, Changed, Message)` — `assign`/`unassign` answer with
  `Changed`, which is `false` when the member already held the role or already didn't. **Neither case
  is an error** — the request describes a target state, and that state now holds.
- `PermissionCatalogueResponse(Permissions)` — the catalogue a client builds a role editor from.

### Error codes

On top of the [user error codes](users.md#error-codes): `invalid_role_name`, `role_name_taken`,
`role_not_found`, `invalid_permission`, `role_immutable`, `role_not_assignable`, `last_owner`,
`member_not_found`, `missing_permission`.

## Related

- [Channels](channels.md#permissions) — what permission each channel operation needs.
- [Events](events.md) — `role.created`, `role.updated`, `role.deleted` and `member.roles` are pushed to
  every member, from the API and the console alike. They are the cue for a client to re-read its channel
  list and `GET /api/roles/me`, since what it may see and do may have changed.
- [Configuration](configuration.md#roles) — the `roles` section of `raindrop.json`.
