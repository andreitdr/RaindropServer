using RaindropServer;
using RaindropServer.Channels.Text;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Channels.Text;

/// <summary>
/// POST /api/channels/text/messages/send — posts a message to a text channel. The message comes back
/// as the server stored it, so the client can replace its optimistic copy with the real id and
/// timestamp.
/// </summary>
public sealed class SendMessageEndpoint : IAuthenticatedTypedEndpoint<SendMessageRequest, ChannelMessage>
{
    private readonly TextChannelService _text;

    public SendMessageEndpoint(TextChannelService text) => _text = text;

    public string Route => "/api/channels/text/messages/send";
    public HttpMethod Method => HttpMethod.Post;

    public Task<ChannelMessage> HandleAsync(
        UserSession session, SendMessageRequest request, CancellationToken cancellationToken) =>
        _text.SendAsync(session, request, cancellationToken);
}
