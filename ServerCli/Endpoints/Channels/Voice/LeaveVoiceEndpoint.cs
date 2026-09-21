using RaindropServer;
using RaindropServer.Channels.Voice;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Channels.Voice;

/// <summary>
/// POST /api/channels/voice/leave — leaves the named call. Idempotent: <c>left</c> is false when this
/// session was not in it, which is not an error.
/// </summary>
public sealed class LeaveVoiceEndpoint : IAuthenticatedTypedEndpoint<LeaveVoiceRequest, LeaveVoiceResponse>
{
    private readonly VoiceService _voice;

    public LeaveVoiceEndpoint(VoiceService voice) => _voice = voice;

    public string Route => "/api/channels/voice/leave";
    public HttpMethod Method => HttpMethod.Post;

    public Task<LeaveVoiceResponse> HandleAsync(
        UserSession session, LeaveVoiceRequest request, CancellationToken cancellationToken) =>
        _voice.LeaveAsync(session, request, cancellationToken);
}
