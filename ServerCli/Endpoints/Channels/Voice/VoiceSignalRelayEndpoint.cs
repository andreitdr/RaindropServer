using RaindropServer;
using RaindropServer.Channels.Voice;

namespace ServerCli.Endpoints.Channels.Voice;

/// <summary>
/// GET /api/channels/voice/signal/{path} — where a client opens its media connection when the
/// transport routes it through this server, so that connection rides the same pinned TLS certificate
/// as everything else. Served by the transport's <see cref="IVoiceSignalRelay"/>; 404 without one.
/// </summary>
/// <remarks>
/// A raw <see cref="IEndpoint"/> on purpose: the caller authenticates with the transport's own
/// credential, not a Raindrop session, so the authenticated adapters would refuse it with 401 before
/// the relay could check it. GET routes also answer CONNECT, so a WebSocket works over HTTP/2 too.
/// </remarks>
public sealed class VoiceSignalRelayEndpoint : IEndpoint
{
    private readonly IVoiceSignalRelay? _relay;

    public VoiceSignalRelayEndpoint(IEnumerable<IVoiceSignalRelay> relays) => _relay = relays.FirstOrDefault();

    public string Route => "/api/channels/voice/signal/{**path}";
    public HttpMethod Method => HttpMethod.Get;

    public Task HandleAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (_relay is null)
            return EndpointResponse.WriteAsync<object>(context, cancellationToken, _ =>
                throw ApiException.NotFound("not_found", "This server does not relay voice connections."));

        var path = context.Request.RouteValues["path"] as string ?? "";
        return _relay.RelayAsync(context, path, cancellationToken);
    }
}
