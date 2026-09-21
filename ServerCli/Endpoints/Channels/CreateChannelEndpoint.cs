using RaindropServer;
using RaindropServer.Channels;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Channels;

/// <summary>
/// POST /api/channels/create — creates a channel of the requested type, <c>text</c> or <c>voice</c>.
/// A type this server has no module for is refused with <c>channel_type_unsupported</c>.
/// </summary>
public sealed class CreateChannelEndpoint : IAuthenticatedTypedEndpoint<CreateChannelRequest, ChannelSummary>
{
    private readonly ChannelService _channels;

    public CreateChannelEndpoint(ChannelService channels) => _channels = channels;

    public string Route => "/api/channels/create";
    public HttpMethod Method => HttpMethod.Post;

    public Task<ChannelSummary> HandleAsync(
        UserSession session, CreateChannelRequest request, CancellationToken cancellationToken) =>
        _channels.CreateAsync(session, request, cancellationToken);
}
