# Endpoint model & DI wiring

## The four endpoint interfaces

Implement one of these anywhere in a loaded assembly and it is mapped automatically — there is no
manual registration. The last three are wrapped into an `IEndpoint` by an adapter, so `ServerHost`
only ever has to know about `IEndpoint`.

| Interface | Gives you | Use when |
|---|---|---|
| [`IEndpoint`](../RaindropServer/IEndpoint.cs) | raw `HttpContext` | full control, or a GET with no request body |
| [`ITypedEndpoint<TReq,TRes>`](../RaindropServer/ITypedEndpoint.cs) | typed request and response | an anonymous POST |
| [`IAuthenticatedEndpoint`](../RaindropServer/IAuthenticatedEndpoint.cs) | `UserSession` + `HttpContext` | an authenticated GET |
| [`IAuthenticatedTypedEndpoint<TReq,TRes>`](../RaindropServer/IAuthenticatedTypedEndpoint.cs) | `UserSession` + typed request and response | an authenticated POST |

The authenticated pair resolve `Authorization: Bearer <token>` through `ISessionStore` and answer 401
before the handler runs, so a handler can assume a valid caller. The typed pair are *format agnostic*
(see [Wire formats](wire-formats.md)) — a handler never knows whether it was called in JSON or binary.

All real endpoints live under `ServerCli/Endpoints/`, mirroring the library's domain folders:
`Users/`, `Members/`, `Roles/`, `Events/`, `Channels/` (with `Channels/Text/` for message endpoints).

A `GET` route is mapped for `CONNECT` too, because over HTTP/2 a WebSocket is opened with an extended
`CONNECT` rather than a `GET` upgrade — that is what lets `GET /api/events` work on either protocol.

## Error handling

Handlers signal failure by throwing [`ApiException`](../RaindropServer/ApiException.cs)
(e.g. `ApiException.Conflict("username_taken", …)`, `ApiException.Forbidden(...)`,
`ApiException.BadRequest(...)`). Every adapter turns that into the right HTTP status code with an
`{ "error", "message" }` body, encoded in whichever wire format the caller negotiated. Raw `IEndpoint`
implementations get the same treatment by going through `EndpointResponse.WriteAsync` themselves.

Adapters (`AuthenticatedEndpointAdapter`, `TypedEndpointAdapter<,>`, `AuthenticatedTypedEndpointAdapter<,>`)
carry `[SkipEndpointDiscoveryAttribute]` so discovery does not try to map them as endpoints in their
own right.

## Discovery

`AddDiscoveredEndpoints` (private, called by `AddRaindropServer`) scans every currently-loaded
assembly for classes implementing one of the four interfaces above (skipping anything marked
`[SkipEndpointDiscovery]`), and registers:

- a plain `IEndpoint` implementation directly;
- an `IAuthenticatedEndpoint` wrapped in `AuthenticatedEndpointAdapter`;
- an `ITypedEndpoint<TReq,TRes>` wrapped in `TypedEndpointAdapter<TReq,TRes>`;
- an `IAuthenticatedTypedEndpoint<TReq,TRes>` wrapped in `AuthenticatedTypedEndpointAdapter<TReq,TRes>`.

Each concrete endpoint type is also registered under its own type (`TryAddTransient`), so an adapter
resolves exactly the implementation it wraps even when two endpoints happen to share a request/response
pair. `ServerHost.Start` then maps every resolved `IEndpoint`.

To register an endpoint that discovery can't find (e.g. its assembly isn't loaded yet), call
`services.AddEndpoint<T>()` directly.

## DI registration surface

All of this lives in [`ServiceCollectionExtensions.cs`](../RaindropServer/ServiceCollectionExtensions.cs).
Every method here uses `TryAdd*`, so a service registered before it runs wins — this is how a host can
swap in a custom `IPermissionResolver` or `IPresenceTracker` before calling the umbrella method.

### `AddRaindropServer(configureUsers?, configureChannels?, configureRoles?, configureEvents?, configureVoice?, configureAttachments?)`

The umbrella call. Registers, in order: routing, `AddRaindropPayloads`, `AddRaindropEvents`, `AddRaindropUsers`,
`AddRaindropRoles`, `AddRaindropMembers`, `AddRaindropAttachments`, `AddRaindropChannels`, `AddRaindropVoice`,
then discovers every endpoint. Attachments go before channels because text channels fill in a refusing
`IMessageAttachments` when nothing registered one first. A
database module still has to be enabled separately — nothing is persisted until `AddRaindropDatabase`
runs.

### `AddRaindropPayloads()`

Registers the wire formats: `JsonPayloadCodec` and `MessagePackPayloadCodec` as an
`IEnumerable<IPayloadCodec>`, plus the `PayloadCodecs` negotiator. Also registers a placeholder
`ServerIdentity` (protocol version, no fingerprint yet) — `ServerCli/Program.cs` replaces it with the
real one once the certificate is loaded.

### `AddRaindropEvents(configure?)`

Registers `EventOptions`, one `EventHub` behind both `IEventPublisher` and `IEventConnectionRegistry`
(and as an `ISessionListener`, so it closes streams whose session is revoked), and the
`WebSocketEventStream` transport. Every subsystem that publishes — users, roles, members, channels —
calls it itself, so each still works registered on its own. Register a different publisher or registry
first to replace it. See [Events](events.md#architecture-and-the-seams-for-plugins).

### `AddServerEventHandler<T, THandler>()`

Registers in-process code to run for every event whose payload is `T` — the hook a plugin uses to react
to what happens on the server. See [Events](events.md).

### `AddRaindropUsers(configure?)`

Registers `UserOptions`, `TimeProvider.System`, `IPasswordHasher` (`Pbkdf2PasswordHasher`),
`ISessionStore` (`InMemorySessionStore`), `UserService`, and `RetentionCleanupService` as a hosted
service. See [Users](users.md).

### `AddRaindropRoles(configure?)`

Registers `RoleOptions`, `RolePermissionCache`, `IPermissionResolver` (`RolePermissionResolver`),
`RoleService`, and `OwnerClaim` as an `IRegistrationListener`. Note: **the built-in roles are not
created here** — they're rows, not DI registrations, seeded later by
`InitializeRaindropDatabaseAsync`. See [Roles & permissions](roles-and-permissions.md).

### `AddRaindropMembers()`

Registers `IPresenceTracker` (`SessionPresenceTracker`) and `MemberService`. See [Members](members.md).

### `AddRaindropChannels(configure?)`

Registers `ChannelOptions`, calls `AddRaindropRoles()` (channels own no permission rules of their own
— see [Channels](channels.md#permissions)), registers `ChannelService`, and registers the text channel
module (`TextChannelModule` as an `IChannelModule`, plus `TextChannelService`), plus the default
`IMessageDeletionListener` (`ArchivingMessageDeletionListener`, added with `TryAddEnumerable` so plugin
listeners run beside it — see [Messages](messages.md#the-deleted-message-archive)). Adding a new channel
type means adding its own module registration here, not editing `ChannelService`.

### `AddRaindropAttachments(configure?)`

Registers `AttachmentOptions`, `AttachmentService` (also as the `IMessageAttachments` text channels use),
the default `IAttachmentStorage` (`LocalFolderAttachmentStorage`), `ConfiguredAttachmentPolicy` and
`AttachmentDeletionListener` (both with `TryAddEnumerable`), `AttachmentMaintenanceService` and
`AttachmentStorageStartupCheck`, then calls `AddRaindropChannels()`. Register a different storage, or an
`IMessageAttachments` of your own, before it runs. See [Attachments](attachments.md#seams-for-plugins).

Its chunk upload (`PUT /api/attachments/uploads/chunk`) and download (`POST /api/attachments/download`) are
**raw `IAuthenticatedEndpoint`s** rather than typed ones, because their bodies are bytes rather than a
negotiated payload: the chunk endpoint raises `IHttpMaxRequestBodySizeFeature` for its own request only,
reads the ids from headers, and answers in the negotiated format through `EndpointResponse.WriteAsync`;
the download endpoint reads its small typed body with `ReadPayloadAsync` and streams
`application/octet-stream`, writing errors with `ApiException.WriteToAsync` before anything is streamed.

### `AddRaindropVoice(configure?)`

Registers `VoiceOptions`, the voice channel module (`VoiceChannelModule`), `VoiceService`, the
in-memory `IVoiceStateStore`, the `VoiceWorkQueue` and `VoiceMaintenanceService` (the hosted service
that reconciles calls with the media server), and the listeners that keep calls in step with sessions
and roles. `IVoiceTransport` falls back to `UnavailableVoiceTransport`, which refuses every join —
register a real one **before** this runs: `AddRaindropLiveKit(configure)` from the
`RaindropServer.Voice.LiveKit` assembly registers LiveKit's transport and signalling relay. See
[Voice](voice.md#seams-for-plugins).

### `AddRaindropDatabase(options)` / `AddRaindropDatabase(configure)`

Resolves one `IDatabaseDriver` by name via `DatabaseDriverRegistry`, and registers it plus the five
stores it creates (`IUserStore`, `IChannelStore`, `IRoleStore`, `ITextMessageStore`,
`IDeletedMessageStore`) as singletons.
**Nothing is persisted until this is called.** See [Database modules](database-modules.md).

### `InitializeRaindropDatabaseAsync()`

Two jobs: create any missing tables (`driver.EnsureSchemaAsync`, only when
`DatabaseOptions.AutoCreateSchema` is set) and seed the two built-in roles if they don't exist yet
(`RoleService.EnsureBuiltInRolesAsync`). Must be called **before** `ServerHost.Start`, on the
registered service instances directly (not through a container), so both are in place before the
first HTTP request can arrive — deliberately not a hosted service, because a hosted service has no
guaranteed ordering against Kestrel, and the very first request to a fresh server is somebody
registering and expecting to own it.

### `ServerHost.Start(services, hostOptions)`

Copies the service descriptors into a `WebApplication`, maps every resolved `IEndpoint`, configures
Kestrel (TLS, HTTP/1.1 + HTTP/2 via ALPN — see [Transport security](transport-security.md)), and starts
on a background `Task.Run` so the calling thread stays free for the console loop. Returns an
`IAsyncDisposable` that exposes `Services`, so callers (the console, tests, anything else) act through
the same instances the endpoints use.
