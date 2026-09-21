using Microsoft.AspNetCore.Http;

namespace RaindropServer;

/// <summary>
/// Implement this interface to register an API endpoint with the server.
/// Register your implementation via DI using <see cref="ServiceCollectionExtensions.AddEndpoint{T}"/>.
/// </summary>
public interface IEndpoint
{
    /// <summary>The route pattern, e.g. "/api/hello".</summary>
    string Route { get; }

    /// <summary>The HTTP method this endpoint responds to, e.g. <see cref="HttpMethod.Get"/>.</summary>
    HttpMethod Method { get; }

    /// <summary>Handles the incoming request and writes the response.</summary>
    Task HandleAsync(HttpContext context, CancellationToken cancellationToken);
}
