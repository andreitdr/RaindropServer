using RaindropServer;
using RaindropServer.Channels;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Channels;

/// <summary>
/// POST /api/channels/delete — deletes a channel and everything in it, for a member who moderates
/// it. Unlike deleting a single message, this leaves nothing behind: the conversation goes with the
/// channel.
/// </summary>
public sealed class DeleteChannelEndpoint : IAuthenticatedTypedEndpoint<ChannelRequest, ChannelDeletedResponse>
{
    private readonly ChannelService _channels;

    public DeleteChannelEndpoint(ChannelService channels) => _channels = channels;

    public string Route => "/api/channels/delete";
    public HttpMethod Method => HttpMethod.Post;

    public Task<ChannelDeletedResponse> HandleAsync(
        UserSession session, ChannelRequest request, CancellationToken cancellationToken) =>
        _channels.DeleteAsync(session, request, cancellationToken);
}
