using RaindropServer;
using RaindropServer.Channels;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Channels;

/// <summary>POST /api/channels/rename — renames a channel, for a member who moderates it.</summary>
public sealed class RenameChannelEndpoint : IAuthenticatedTypedEndpoint<RenameChannelRequest, ChannelSummary>
{
    private readonly ChannelService _channels;

    public RenameChannelEndpoint(ChannelService channels) => _channels = channels;

    public string Route => "/api/channels/rename";
    public HttpMethod Method => HttpMethod.Post;

    public Task<ChannelSummary> HandleAsync(
        UserSession session, RenameChannelRequest request, CancellationToken cancellationToken) =>
        _channels.RenameAsync(session, request, cancellationToken);
}
