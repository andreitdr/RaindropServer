using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace RaindropServer.Channels.Voice;

/// <summary>
/// Serves <see cref="Path"/> by handing each request to the transport's <see cref="IVoiceSignalRelay"/>,
/// so a client's media connection rides the same pinned TLS certificate as its gRPC calls. Mapped only
/// when a transport registered a relay; without one the path does not exist.
/// </summary>
/// <remarks>
/// This is the one route on the server that is not gRPC: it carries the media transport's own
/// signalling protocol (LiveKit's, a WebSocket carrying LiveKit's protobuf), which its client SDK
/// speaks and Raindrop only forwards. The caller authenticates with the transport's credential rather
/// than a Raindrop session, so nothing of Raindrop's own authentication runs here.
/// </remarks>
public sealed class VoiceSignalRoutes : IRouteContributor
{
    /// <summary>Where clients connect; a join answers this as its <c>path</c>.</summary>
    public const string Path = "/api/channels/voice/signal";

    /// <inheritdoc />
    public void Map(WebApplication app)
    {
        if (app.Services.GetServices<IVoiceSignalRelay>().FirstOrDefault() is not { } relay)
            return;

        // The middleware pings an idle signalling socket, so that proxies and NAT boxes keep it open.
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

        // A WebSocket over HTTP/2 is an extended CONNECT rather than a GET upgrade, so the route answers
        // both; LiveKit's SDK opens it over HTTP/1.1 today.
        app.MapMethods($"{Path}/{{**path}}", [HttpMethods.Get, HttpMethods.Connect],
            (HttpContext context, CancellationToken cancellationToken) =>
                relay.RelayAsync(context, context.Request.RouteValues["path"] as string ?? "", cancellationToken));
    }
}
