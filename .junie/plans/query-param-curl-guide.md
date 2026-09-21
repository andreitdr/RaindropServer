---
sessionId: session-260821-144624-1wkn
---

# JSON Input & Output for Endpoints

## Requirements

### Overview & Goals
Allow endpoints to receive JSON request bodies and return JSON responses, using both the existing raw `IEndpoint` approach and a new ergonomic typed generic interface.

### Scope
**In Scope:**
- Document how to read JSON from the request body and write JSON to the response inside `HandleAsync`
- Add a generic `IEndpoint<TRequest, TResponse>` interface to the `RaindropServer` library
- Add auto-discovery support for the new typed interface in `ServiceCollectionExtensions`
- Provide an example typed JSON endpoint in `ServerCli`

**Out of Scope:**
- Custom JSON serializer options (uses `System.Text.Json` defaults)
- OpenAPI / Swagger schema generation

### User Stories
- As a developer, I want to declare the request and response types in the interface so I don't write JSON boilerplate in every endpoint.
- As a developer, I want the library to handle deserialization of the request body and serialization of the response automatically.

## Technical Design

### Current Implementation
- `IEndpoint` in `RaindropServer/IEndpoint.cs` exposes `Route`, `Method`, and `HandleAsync(HttpContext, CancellationToken)`. JSON access today requires calling `context.Request.ReadFromJsonAsync<T>()` and `context.Response.WriteAsJsonAsync(obj)` manually inside `HandleAsync`.
- `ServiceCollectionExtensions.AddRaindropServer()` auto-discovers all `IEndpoint` implementations via `AppDomain.CurrentDomain.GetAssemblies()`.
- `ServerHost.Start()` in `RaindropServer/ServerHost.cs` maps discovered endpoints using `app.MapMethods`.

### Key Decisions
- **Typed interface via `IEndpoint<TRequest, TResponse>`** — a second interface that declares the request/response types. The library adapter calls `ReadFromJsonAsync` / `WriteAsJsonAsync` automatically, so the implementor only writes business logic.
- The raw `IEndpoint` remains unchanged — both interfaces are supported side-by-side.
- `System.Text.Json` (already included with ASP.NET Core) is used; no extra packages needed.

### Proposed Changes

#### 1. Raw approach (no code change needed)
Any existing `IEndpoint.HandleAsync` implementation can already read/write JSON:
```csharp
public async Task HandleAsync(HttpContext context, CancellationToken ct)
{
    var request = await context.Request.ReadFromJsonAsync<MyRequest>(ct);
    var response = new MyResponse { Message = $"Hello {request?.Name}" };
    await context.Response.WriteAsJsonAsync(response, ct);
}
```

#### 2. New `IEndpoint<TRequest, TResponse>` interface (`RaindropServer/IJsonEndpoint.cs`)
```csharp
public interface IJsonEndpoint<TRequest, TResponse>
{
    string Route { get; }
    HttpMethod Method { get; }
    Task<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken);
}
```
The library wraps each implementation in an adapter that:
1. Calls `ReadFromJsonAsync<TRequest>()` on the request body.
2. Invokes `HandleAsync(request, ct)`.
3. Calls `WriteAsJsonAsync(response)` on the result.

#### 3. Auto-discovery update (`RaindropServer/ServiceCollectionExtensions.cs`)
`AddRaindropServer()` already scans assemblies for `IEndpoint`. The same scan will be extended to detect closed generic implementations of `IJsonEndpoint<,>` and register their adapter wrapper.

#### 4. Adapter class (`RaindropServer/JsonEndpointAdapter.cs`)
An internal `JsonEndpointAdapter<TRequest, TResponse>` wraps `IJsonEndpoint<TRequest, TResponse>` as an `IEndpoint` so `ServerHost` needs no changes.

#### 5. Example endpoint (`ServerCli/Endpoints/GreetJsonEndpoint.cs`)
```csharp
public sealed class GreetJsonEndpoint : IJsonEndpoint<GreetRequest, GreetResponse>
{
    public string Route => "/api/greet";
    public HttpMethod Method => HttpMethod.Post;

    public Task<GreetResponse> HandleAsync(GreetRequest request, CancellationToken ct)
        => Task.FromResult(new GreetResponse { Message = $"Hello {request.Name}!" });
}

public record GreetRequest(string Name);
public record GreetResponse(string Message);
```

curl example:
```bash
curl -X POST http://localhost:5000/api/greet \
     -H "Content-Type: application/json" \
     -d '{"name": "Alice"}'

# Response: {"message":"Hello Alice!"}

```

### File Structure
| File | Change |
|------|--------|
| `RaindropServer/IJsonEndpoint.cs` | **New** — generic typed interface |
| `RaindropServer/JsonEndpointAdapter.cs` | **New** — adapter wrapping typed interface as `IEndpoint` |
| `RaindropServer/ServiceCollectionExtensions.cs` | **Modified** — scan for `IJsonEndpoint<,>` implementations |
| `ServerCli/Endpoints/GreetJsonEndpoint.cs` | **New** — example JSON endpoint |

# Delivery Steps

### ✓ Step 1: Add `IJsonEndpoint<TRequest, TResponse>` interface and its adapter to the library
The library can host typed JSON endpoints via a new generic interface backed by an internal adapter.

- Create `RaindropServer/IJsonEndpoint.cs` with the `IJsonEndpoint<TRequest, TResponse>` interface (`Route`, `Method`, `HandleAsync(TRequest, CancellationToken) → Task<TResponse>`).
- Create `RaindropServer/JsonEndpointAdapter.cs` — internal sealed class that implements `IEndpoint`, wraps an `IJsonEndpoint<TRequest, TResponse>`, calls `ReadFromJsonAsync<TRequest>` on the request, and `WriteAsJsonAsync` on the response.

### ✓ Step 2: Extend auto-discovery to find typed JSON endpoints
Implementing `IJsonEndpoint<TRequest, TResponse>` is enough — no manual registration needed.

- Update `AddRaindropServer()` in `ServiceCollectionExtensions.cs` to additionally scan for all concrete classes implementing a closed generic `IJsonEndpoint<,>`.
- For each found type, register a `JsonEndpointAdapter<TRequest, TResponse>` as an `IEndpoint` in the DI container.

### ✓ Step 3: Add an example JSON endpoint to `ServerCli`
Developers can see a full working example of the typed JSON pattern.

- Create `ServerCli/Endpoints/GreetJsonEndpoint.cs` with `GreetRequest` / `GreetResponse` records and `GreetJsonEndpoint : IJsonEndpoint<GreetRequest, GreetResponse>` handling `POST /api/greet`.
- Verify with `dotnet build` and a curl command returning `{"message":"Hello Alice!"}`.