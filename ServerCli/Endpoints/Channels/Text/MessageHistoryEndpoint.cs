using RaindropServer;
using RaindropServer.Channels.Text;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Channels.Text;

/// <summary>
/// POST /api/channels/text/messages/history — one page of a channel's messages. The first call
/// returns the newest page; passing the <c>nextCursor</c> back walks further into the past, until it
/// comes back null at the beginning of the channel.
/// </summary>
public sealed class MessageHistoryEndpoint
    : IAuthenticatedTypedEndpoint<MessageHistoryRequest, MessageHistoryResponse>
{
    private readonly TextChannelService _text;

    public MessageHistoryEndpoint(TextChannelService text) => _text = text;

    public string Route => "/api/channels/text/messages/history";
    public HttpMethod Method => HttpMethod.Post;

    public Task<MessageHistoryResponse> HandleAsync(
        UserSession session, MessageHistoryRequest request, CancellationToken cancellationToken) =>
        _text.ReadHistoryAsync(session, request, cancellationToken);
}
