using RaindropServer;
using RaindropServer.Channels;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Channels;

/// <summary>
/// POST /api/channels/get — one channel, and what the caller may do in it. A POST rather than a GET
/// with the id in the path, so channel ids stay inside the encrypted body like every other id here.
/// </summary>
public sealed class GetChannelEndpoint : IAuthenticatedTypedEndpoint<ChannelRequest, ChannelSummary>
{
    private readonly ChannelService _channels;

    public GetChannelEndpoint(ChannelService channels) => _channels = channels;

    public string Route => "/api/channels/get";
    public HttpMethod Method => HttpMethod.Post;

    public Task<ChannelSummary> HandleAsync(
        UserSession session, ChannelRequest request, CancellationToken cancellationToken) =>
        _channels.GetAsync(session, request, cancellationToken);
}
