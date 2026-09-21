using Microsoft.AspNetCore.Http;
using RaindropServer.Payloads;

namespace RaindropServer;

/// <summary>
/// Helpers for raw <see cref="IEndpoint"/> and <see cref="IAuthenticatedEndpoint"/> implementations,
/// which have no adapter working on their behalf. They get the same wire-format negotiation and
/// error handling the typed adapters apply automatically.
/// </summary>
public static class EndpointResponse
{
    /// <summary>
    /// Runs <paramref name="produce"/> and writes its result in the caller's format, or its
    /// <see cref="ApiException"/> as an error body.
    /// </summary>
    public static async Task WriteAsync<T>(
        HttpContext context, CancellationToken cancellationToken, Func<CancellationToken, Task<T>> produce)
    {
        try
        {
            var result = await produce(cancellationToken);
            await context.WritePayloadAsync(result, cancellationToken);
        }
        catch (ApiException ex)
        {
            await ex.WriteToAsync(context, cancellationToken);
        }
    }
}
