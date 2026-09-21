using Microsoft.AspNetCore.Http;
using RaindropServer.Payloads;

namespace RaindropServer;

/// <summary>
/// Wraps an <see cref="ITypedEndpoint{TRequest,TResponse}"/> as an <see cref="IEndpoint"/> so that
/// <see cref="ServerHost"/> can map it without any changes. The adapter negotiates the wire format,
/// deserializes the request, serializes the response, and turns an <see cref="ApiException"/> thrown
/// by the handler into an error response.
/// </summary>
[SkipEndpointDiscovery]
internal sealed class TypedEndpointAdapter<TRequest, TResponse> : IEndpoint
{
    private readonly ITypedEndpoint<TRequest, TResponse> _inner;
    private readonly PayloadCodecs _codecs;

    public TypedEndpointAdapter(ITypedEndpoint<TRequest, TResponse> inner, PayloadCodecs codecs)
    {
        _inner = inner;
        _codecs = codecs;
    }

    public string Route => _inner.Route;
    public HttpMethod Method => _inner.Method;

    public async Task HandleAsync(HttpContext context, CancellationToken cancellationToken)
    {
        try
        {
            var codec = _codecs.ForRequest(context.Request);
            var request = await context.ReadPayloadAsync<TRequest>(codec, cancellationToken);
            var response = await _inner.HandleAsync(request, cancellationToken);
            await context.WritePayloadAsync(response, cancellationToken);
        }
        catch (ApiException ex)
        {
            await ex.WriteToAsync(context, cancellationToken);
        }
    }
}
