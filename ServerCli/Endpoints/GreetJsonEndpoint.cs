using RaindropServer;

namespace ServerCli.Endpoints;

/// <summary>
/// Example typed JSON endpoint: POST /api/greet
/// Implement <see cref="IJsonEndpoint{TRequest,TResponse}"/> — the library handles JSON deserialization
/// and serialization automatically. No boilerplate needed.
/// </summary>
public sealed class GreetJsonEndpoint : ITypedEndpoint<GreetRequest, GreetResponse>
{
    public string Route => "/api/greet";
    public HttpMethod Method => HttpMethod.Post;

    public Task<GreetResponse> HandleAsync(GreetRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new GreetResponse($"Hello {request.Name}!"));
}

/// <param name="Name">The name to greet.</param>
public record GreetRequest(string Name);

/// <param name="Message">The greeting message returned as JSON.</param>
public record GreetResponse(string Message);
