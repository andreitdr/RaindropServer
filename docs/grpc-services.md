# gRPC services & DI wiring

Every client talks to this server over **gRPC**: HTTP/2 inside TLS 1.3, protobuf messages, defined
in the sibling [RaindropProto](../../RaindropProto/README.md) repository (package `raindrop.v1`).
There is no REST API and no JSON on the wire. The one route that is not gRPC is the voice signalling
relay (see [below](#the-one-route-that-is-not-grpc)).

## Where things live

| Piece | Where |
|---|---|
| The protocol: every service, message and enum | `../RaindropProto/raindrop/v1/*.proto`, a sibling checkout |
| Generated C# (`RaindropServer.Grpc.Protocol`) | built from those files by `Grpc.Tools` on every `dotnet build`, into `obj/`, never committed |
| The services that implement them | [`RaindropServer.Grpc/Services/`](../RaindropServer.Grpc/Services/) |
| Domain records ⇄ protobuf | [`RaindropServer.Grpc/Mapping/`](../RaindropServer.Grpc/Mapping/) (`Wire` for scalars, `ToWire` for records) |
| Authentication and error mapping | [`RaindropInterceptor`](../RaindropServer.Grpc/RaindropInterceptor.cs) |
| The event stream transport | [`RaindropServer.Grpc/Events/`](../RaindropServer.Grpc/Events/) |
| Every rule | the library, `RaindropServer/` — unchanged by the transport |

The services are thin on purpose. Each RPC reads its request, calls the library service that owns the
rule (`UserService`, `RoleService`, `TextChannelService`, `MlsDeliveryService`, …), and maps the record
it returns. The library's contract records (`Users/Contracts.cs`, `Channels/ChannelContracts.cs`, …)
are not protobuf types. They are what the domain returns, the console uses them too, and the library
never references the generated code. `RaindropServer.csproj` has no gRPC or protobuf package at all.

`RaindropServer.Grpc` reads the protos from `../../RaindropProto`, relative to itself. CI checks that
repository out beside this one, and the Docker build receives it as a second build context (see
[Local development & Docker](local-development-and-docker.md)).

## The services

All in package `raindrop.v1`. Every RPC needs `authorization: Bearer <token>` metadata unless it is
marked anonymous.

| Service (`.proto`) | RPCs | Covered in |
|---|---|---|
| `ServerService` (`server.proto`) | `GetServerInfo` *(anonymous)* | [Transport security](transport-security.md) |
| `UserService` (`users.proto`) | `GetUserStatus`, `CheckUsername`, `Register`, `Login`, `Rejoin` *(all anonymous)*, `Logout`, `GetMe`, `LookupUser`, `ChangePassword`, `ChangeUsername`, `LeaveServer` | [Users](users.md#user-api) |
| `MemberService` (`members.proto`) | `ListMembers` | [Members](members.md#member-api) |
| `RoleService` (`roles.proto`) | `ListRoles`, `GetMyRoles`, `ListPermissions`, `GetRole`, `CreateRole`, `UpdateRole`, `DeleteRole`, `GetMemberRoles`, `AssignRole`, `UnassignRole` | [Roles & permissions](roles-and-permissions.md#role-api) |
| `ChannelService` (`channels.proto`) | `ListChannels`, `GetChannel`, `CreateChannel`, `RenameChannel`, `DeleteChannel` | [Channels](channels.md#channel-api) |
| `TextChannelService` (`text.proto`) | `SendMessage`, `SendEncryptedMessage`, `GetHistory`, `DeleteMessage`, `ReportMessage`, `ListReports` | [Messages](messages.md#message-api) |
| `VoiceService` (`voice.proto`) | `GetVoiceStatus`, `JoinVoice`, `LeaveVoice`, `SetVoiceSelfState`, `MuteVoiceMember` | [Voice](voice.md#voice-api) |
| `DeviceService` (`devices.proto`) | `RegisterDevice`, `ListDevices`, `RevokeDevice`, `ResetIdentity`, `CreateSessionChallenge`, `BindSession`, `GetKeyBackup`, `PutKeyBackup`, `StartDeviceLink`, `GetDeviceLinkOffer`, `ApproveDeviceLink`, `TakeDeviceLinkApproval` | [E2EE](e2ee.md#api) |
| `DeliveryService` (`delivery.proto`) | `CreateGroup`, `GetGroupState`, `SubmitCommit`, `PutGroupInfo`, `SyncGroup`, `GetGroupRoster`, `UploadKeyPackages`, `ClaimKeyPackages`, `GetKeyPackageCount`, `ListWelcomes`, `AckWelcomes` | [E2EE](e2ee.md#api) |
| `AttachmentService` (`attachments.proto`) | `GetAttachmentPolicy`, `CreateUpload`, `Upload` *(client stream)*, `GetUploadStatus`, `CompleteUpload`, `CancelUpload`, `Download` *(server stream)* | [Attachments](attachments.md#attachment-api) |
| `EventService` (`events.proto`) | `Connect` *(bidirectional stream)* | [Events](events.md) |

## Authentication

[`RaindropInterceptor`](../RaindropServer.Grpc/RaindropInterceptor.cs) runs around every call of every
shape (unary, client, server and duplex streaming):

- It reads `authorization: Bearer <token>` and resolves it through `ISessionStore.TryValidate`, which
  also slides the session.
  - No token gives `UNAUTHENTICATED` with reason `missing_token`.
  - An unknown or lapsed token gives `invalid_session`.
  - The handler only runs once the caller is resolved, and reads the session with
    `context.Session()`.
- A method marked `[AllowAnonymous]` skips this step. The interceptor reads the attribute from the
  endpoint metadata ASP.NET Core attaches to each gRPC method, so the marker sits on the override
  itself. Six RPCs carry it: the handshake and the five sign-in calls.

## Errors

Services and the library signal failure by throwing [`ApiException`](../RaindropServer/ApiException.cs),
for example `ApiException.Conflict("username_taken", …)`. The exception's `ApiErrorKind` decides the
gRPC status, and its code becomes the `google.rpc.ErrorInfo.reason` in the `grpc-status-details-bin`
trailer (domain `raindrop`). Clients branch on the reason, never on the status.

| `ApiErrorKind` (factory) | gRPC status |
|---|---|
| `BadRequest` | `INVALID_ARGUMENT` |
| `Unauthorized` | `UNAUTHENTICATED` |
| `Forbidden` | `PERMISSION_DENIED` |
| `NotFound` | `NOT_FOUND` |
| `Conflict` | `FAILED_PRECONDITION` |
| `TooLarge` | `RESOURCE_EXHAUSTED` |
| `ServiceUnavailable` | `UNAVAILABLE` |
| anything that is not an `ApiException` | `INTERNAL`, reason `internal_error` |

For an unexpected exception, the interceptor logs it to the server's console. The client gets only
the generic message. Every reason the server produces is listed in
[RaindropProto's errors doc](../../RaindropProto/docs/errors.md).

Ids arrive as strings. [`Wire.Id`](../RaindropServer.Grpc/Mapping/Wire.cs) handles them in two ways:
- **A malformed id** is refused as `invalid_payload`, naming the field.
- **An empty id** becomes `Guid.Empty`, which the domain refuses with the same code it always used for
  a missing one (`invalid_user_id`, `channel_not_found`, …).

proto3 cannot send null, so an optional value is absent on the wire. Where absent and empty mean
different things, the protocol uses `optional` or a wrapper message, and the services map that back to
null. Examples:
- `UpdateRoleRequest.permissions`
- `ReportMessageRequest.attested_files`, which selects the v2 attestation
- `AttachmentPolicy.allowed_extensions`

## Limits

`AddRaindropGrpc` sets the largest message either side may send to 1 MiB
(`GrpcServiceCollectionExtensions.MaximumMessageBytes`). Every call is far smaller except attachment
frames, which are `AttachmentGrpcService.FrameBytes` (256 KiB).

## The one route that is not gRPC

`/api/channels/voice/signal/{path}` carries LiveKit's own signalling protocol: a WebSocket with
LiveKit's protobuf inside. LiveKit's client SDK speaks it, and Raindrop only forwards it
([Voice](voice.md#the-signalling-relay)). It is mapped by
[`VoiceSignalRoutes`](../RaindropServer/Channels/Voice/VoiceSignalRoutes.cs) only when a transport
registers an `IVoiceSignalRelay`. That is also why Kestrel still speaks HTTP/1.1 next to HTTP/2 (ALPN
picks per connection): dart:io opens WebSockets over HTTP/1.1.

## Routes: `IRouteContributor`

[`ServerHost`](../RaindropServer/ServerHost.cs) knows nothing about any transport. It maps every
registered [`IRouteContributor`](../RaindropServer/IRouteContributor.cs). There are two today:
- `AddRaindropGrpc`'s, which maps the services.
- `AddRaindropVoice`'s, `VoiceSignalRoutes`.

A plugin that serves something of its own registers one more with `TryAddEnumerable`.

## DI registration surface

The domain lives in [`ServiceCollectionExtensions.cs`](../RaindropServer/ServiceCollectionExtensions.cs).
The transport lives in
[`GrpcServiceCollectionExtensions.cs`](../RaindropServer.Grpc/GrpcServiceCollectionExtensions.cs).
Every method uses `TryAdd*`, so a service registered before it runs wins. That is how a host swaps in
a custom `IPermissionResolver` or `IPresenceTracker` before calling the umbrella method.

### `AddRaindropServer(configureUsers?, configureChannels?, configureRoles?, configureEvents?, configureVoice?, configureAttachments?)`

The umbrella call for the domain. It registers, in order:
1. routing and `AddRaindropEvents`
2. `AddRaindropUsers`, `AddRaindropRoles` and `AddRaindropMembers`
3. `AddRaindropAttachments` and `AddRaindropChannels`
4. `AddRaindropVoice` and `AddRaindropE2ee`

Attachments go before channels because text channels fill in a refusing `IMessageAttachments` when
nothing registered one first. It registers no transport, and a database module still has to be enabled
separately.

### `AddRaindropGrpc()`

Serves the domain over gRPC. It registers:
- `AddGrpc`, with `RaindropInterceptor` and the 1 MiB message limits
- the `ProtobufEventEncoder`
- the route contributor that maps every service

Call it after `AddRaindropServer`.

### `AddRaindropEvents(configure?)`

Registers:
- `EventOptions`
- one `EventHub` behind both `IEventPublisher` and `IEventConnectionRegistry`, and also as an
  `ISessionListener`, so it closes streams whose session is revoked
- a placeholder `ServerIdentity` (protocol version, no fingerprint), which `ServerCli/Program.cs`
  replaces with the real one once the certificate is loaded

Every subsystem that publishes calls it itself, so each still works when registered on its own. See
[Events](events.md#architecture-and-the-seams-for-plugins).

### `AddServerEventHandler<T, THandler>()`

Registers in-process code to run for every event whose payload is `T`. This is the hook a plugin uses to
react to what happens on the server. See [Events](events.md).

### `AddRaindropUsers(configure?)`

Registers `UserOptions`, `TimeProvider.System`, `IPasswordHasher` (`Pbkdf2PasswordHasher`),
`ISessionStore` (`InMemorySessionStore`), `UserService`, and `RetentionCleanupService` and
`SessionSweepService` as hosted services. See [Users](users.md).

### `AddRaindropRoles(configure?)`

Registers `RoleOptions`, `RolePermissionCache`, `IPermissionResolver` (`RolePermissionResolver`),
`RoleService`, and `OwnerClaim` as an `IRegistrationListener`.

**The built-in roles are not created here.** They are rows, not DI registrations, and are seeded later
by `InitializeRaindropDatabaseAsync`. See [Roles & permissions](roles-and-permissions.md).

### `AddRaindropMembers()`

Registers:
- `IPresenceTracker` (`StreamPresenceTracker`: online while an event stream is open)
- `MemberService`
- `PresenceBroadcaster`, as both an `ISessionListener` and an `IEventStreamListener`

See [Members](members.md).

### `AddRaindropChannels(configure?)`

Registers:
- `ChannelOptions`
- `ChannelService`
- the text channel module: `TextChannelModule` as an `IChannelModule`, plus `TextChannelService`
- the default `IMessageDeletionListener` (`ArchivingMessageDeletionListener`), added with
  `TryAddEnumerable` so plugin listeners run beside it. See
  [Messages](messages.md#the-deleted-message-archive).

It also calls `AddRaindropRoles()`, because channels own no permission rules of their own (see
[Channels](channels.md#permissions)).

Adding a new channel type means adding its own module registration here, not editing `ChannelService`.

### `AddRaindropAttachments(configure?)`

Registers:
- `AttachmentOptions`
- `AttachmentService`, also as the `IMessageAttachments` text channels use
- the default `IAttachmentStorage` (`LocalFolderAttachmentStorage`)
- `ConfiguredAttachmentPolicy` and `AttachmentDeletionListener`, both with `TryAddEnumerable`
- `AttachmentMaintenanceService` and `AttachmentStorageStartupCheck`

It then calls `AddRaindropChannels()`. Register a different storage, or an `IMessageAttachments` of your
own, before it runs. See [Attachments](attachments.md#seams-for-plugins).

### `AddRaindropVoice(configure?)`

Registers:
- `VoiceOptions`
- the voice channel module (`VoiceChannelModule`)
- `VoiceService`
- the in-memory `IVoiceStateStore`
- the `VoiceWorkQueue`, and `VoiceMaintenanceService`, the hosted service that reconciles calls with
  the media server
- the listeners that keep calls in step with sessions and roles
- `VoiceSignalRoutes`

`IVoiceTransport` falls back to `UnavailableVoiceTransport`, which refuses every join. Register a real
one **before** this runs: `AddRaindropLiveKit(configure)` from the `RaindropServer.Voice.LiveKit`
assembly registers LiveKit's transport and signalling relay. See [Voice](voice.md#seams-for-plugins).

### `AddRaindropDatabase(options)` / `AddRaindropDatabase(configure)`

Resolves one `IDatabaseDriver` by name via `DatabaseDriverRegistry`, and registers it plus every store
it creates as singletons. **Nothing is persisted until this is called.** See
[Database modules](database-modules.md).

### `InitializeRaindropDatabaseAsync()`

It has two jobs:
1. Create any missing tables (`driver.EnsureSchemaAsync`, only when `DatabaseOptions.AutoCreateSchema`
   is set).
2. Seed the two built-in roles if they don't exist yet (`RoleService.EnsureBuiltInRolesAsync`).

It must be called **before** `ServerHost.Start`, on the registered service instances directly (not
through a container), so both are in place before the first call can arrive. It is deliberately not a
hosted service: a hosted service has no guaranteed ordering against Kestrel, and the very first call
to a fresh server is somebody registering and expecting to own it.

### `ServerHost.Start(services, hostOptions)` / `ServerHost.Build(services, hostOptions, configure?)`

`Build`:
1. copies the service descriptors into a `WebApplication`
2. configures Kestrel (TLS, HTTP/1.1 + HTTP/2 via ALPN; see [Transport security](transport-security.md))
3. maps every `IRouteContributor`

`Start` validates the options, builds the application, and starts it on a background `Task.Run`, so the
calling thread stays free for the console loop. It returns an `IAsyncDisposable` that exposes
`Services`, so callers (the console, tests, anything else) act through the same instances the services
use.

`Build` on its own is what the tests use. The `configure` callback swaps Kestrel for ASP.NET Core's
in-memory `TestServer` (`RaindropServer.Tests/GrpcTests.cs`), so they call the real services through a
real gRPC client without a certificate or a port.

## Trying it by hand

`grpcurl` speaks gRPC from a terminal. The server presents a self-signed certificate, so pass
`-insecure`, and point it at the protos (the server does not serve reflection):

```bash
grpcurl -insecure -import-path ../RaindropProto -proto raindrop/v1/server.proto localhost:5001 raindrop.v1.ServerService/GetServerInfo
```

Authenticated calls carry the token as a header:

```bash
grpcurl -insecure -import-path ../RaindropProto -proto raindrop/v1/members.proto -H "authorization: Bearer <token>" localhost:5001 raindrop.v1.MemberService/ListMembers
```

`ServerCli probe https://localhost:5001` prints the handshake as `key=value` lines. `deploy.sh` uses it
to read the identity key.
