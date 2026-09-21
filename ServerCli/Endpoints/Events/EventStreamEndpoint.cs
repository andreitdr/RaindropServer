using RaindropServer;
using RaindropServer.Events.WebSockets;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Events;

/// <summary>
/// GET /api/events — the event stream: a WebSocket down which the server pushes every change the
/// caller is allowed to see, as it happens. Opened with the same <c>Authorization: Bearer</c> header
/// as any other call, and in the same wire format as the <c>Accept</c> header asks for.
/// </summary>
public sealed class EventStreamEndpoint : IAuthenticatedEndpoint
{
    private readonly WebSocketEventStream _stream;

    public EventStreamEndpoint(WebSocketEventStream stream) => _stream = stream;

    public string Route => "/api/events";
    public HttpMethod Method => HttpMethod.Get;

    public Task HandleAsync(UserSession session, HttpContext context, CancellationToken cancellationToken) =>
        _stream.AcceptAsync(session, context);
}
