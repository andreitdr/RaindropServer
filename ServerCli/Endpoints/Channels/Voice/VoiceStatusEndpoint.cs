using RaindropServer;
using RaindropServer.Channels.Voice;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Channels.Voice;

/// <summary>
/// GET /api/channels/voice — whether voice calls work on this server at all, and who is in every call
/// in a channel the caller may see. What a client draws the people under each voice channel from.
/// </summary>
public sealed class VoiceStatusEndpoint : IAuthenticatedEndpoint
{
    private readonly VoiceService _voice;

    public VoiceStatusEndpoint(VoiceService voice) => _voice = voice;

    public string Route => "/api/channels/voice";
    public HttpMethod Method => HttpMethod.Get;

    public Task HandleAsync(UserSession session, HttpContext context, CancellationToken cancellationToken) =>
        EndpointResponse.WriteAsync(context, cancellationToken, ct => _voice.StatusAsync(session, ct));
}
