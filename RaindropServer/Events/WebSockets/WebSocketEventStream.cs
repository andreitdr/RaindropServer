using Microsoft.AspNetCore.Http;
using RaindropServer.Payloads;
using RaindropServer.Users;

namespace RaindropServer.Events.WebSockets;

/// <summary>
/// The WebSocket transport for events: upgrades an authenticated request and serves the connection
/// until it closes. The endpoint that exposes it is a one-liner in <c>ServerCli</c>; everything a
/// transport has to get right lives here.
/// </summary>
public sealed class WebSocketEventStream
{
    private readonly IEventConnectionRegistry _registry;
    private readonly PayloadCodecs _codecs;
    private readonly EventOptions _options;

    public WebSocketEventStream(IEventConnectionRegistry registry, PayloadCodecs codecs, EventOptions options)
    {
        _registry = registry;
        _codecs = codecs;
        _options = options;
    }

    /// <summary>
    /// Upgrades the request and serves events down it. The wire format is chosen from <c>Accept</c>
    /// exactly as a response body's would be, so a client asks for MessagePack the same way on the
    /// stream as on every other call.
    /// </summary>
    /// <exception cref="ApiException">The request is not a WebSocket upgrade.</exception>
    public async Task AcceptAsync(UserSession session, HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.Headers.Upgrade = "websocket";
            throw new ApiException(StatusCodes.Status426UpgradeRequired, "websocket_required",
                "This endpoint is a WebSocket. Open it with a WebSocket client, carrying the same " +
                "'Authorization: Bearer <token>' header as any other call.");
        }

        var codec = _codecs.ForResponse(context.Request);
        using var socket = await context.WebSockets.AcceptWebSocketAsync();

        var connection = new WebSocketEventConnection(session, codec, socket, _options);

        using (_registry.Register(connection))
            await connection.RunAsync(context.RequestAborted);
    }
}
