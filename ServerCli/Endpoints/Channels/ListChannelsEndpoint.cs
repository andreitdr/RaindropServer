using RaindropServer;
using RaindropServer.Channels;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Channels;

/// <summary>
/// GET /api/channels — every channel on this server the caller may see, of every type, with what
/// they are allowed to do in each. This is what a client draws its channel list from.
/// </summary>
public sealed class ListChannelsEndpoint : IAuthenticatedEndpoint
{
    private readonly ChannelService _channels;

    public ListChannelsEndpoint(ChannelService channels) => _channels = channels;

    public string Route => "/api/channels";
    public HttpMethod Method => HttpMethod.Get;

    public Task HandleAsync(UserSession session, HttpContext context, CancellationToken cancellationToken) =>
        EndpointResponse.WriteAsync(context, cancellationToken, ct => _channels.ListAsync(session, ct));
}
