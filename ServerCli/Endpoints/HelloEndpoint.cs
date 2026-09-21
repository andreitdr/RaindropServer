using RaindropServer;

namespace ServerCli.Endpoints;

/// <summary>
/// Example endpoint: GET /api/hello
/// Implement <see cref="IEndpoint"/> and register via AddEndpoint&lt;T&gt; — that's all.
/// </summary>
public sealed class HelloEndpoint : IEndpoint
{
    public string Route => "/api/hello";
    public HttpMethod Method => HttpMethod.Get;

    public async Task HandleAsync(HttpContext context, CancellationToken cancellationToken)
    {
        string? input = context.Request.Query["name"];
        await context.Response.WriteAsync($"Hello {input}", cancellationToken);
    }
}
