using RaindropServer;
using RaindropServer.Channels.Text;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Channels.Text;

/// <summary>
/// POST /api/channels/text/messages/send-encrypted — posts an end-to-end encrypted message: an MLS
/// application message for the channel's group, which the server stores and relays without being able
/// to read it. Needs a session bound to one of the author's devices.
/// </summary>
public sealed class SendEncryptedMessageEndpoint : IAuthenticatedTypedEndpoint<SendEncryptedMessageRequest, ChannelMessage>
{
    private readonly TextChannelService _text;

    public SendEncryptedMessageEndpoint(TextChannelService text) => _text = text;

    public string Route => "/api/channels/text/messages/send-encrypted";
    public HttpMethod Method => HttpMethod.Post;

    public Task<ChannelMessage> HandleAsync(
        UserSession session, SendEncryptedMessageRequest request, CancellationToken cancellationToken) =>
        _text.SendEncryptedAsync(session, request, cancellationToken);
}
