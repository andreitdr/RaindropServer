using RaindropServer;
using RaindropServer.Channels.Text;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Channels.Text;

/// <summary>
/// POST /api/channels/text/messages/delete — deletes a message. Authors may always delete their own;
/// deleting somebody else's needs permission to moderate that channel. What comes back is the
/// tombstone that replaces it: same id, same author, no text.
/// </summary>
public sealed class DeleteMessageEndpoint : IAuthenticatedTypedEndpoint<DeleteMessageRequest, ChannelMessage>
{
    private readonly TextChannelService _text;

    public DeleteMessageEndpoint(TextChannelService text) => _text = text;

    public string Route => "/api/channels/text/messages/delete";
    public HttpMethod Method => HttpMethod.Post;

    public Task<ChannelMessage> HandleAsync(
        UserSession session, DeleteMessageRequest request, CancellationToken cancellationToken) =>
        _text.DeleteAsync(session, request, cancellationToken);
}
