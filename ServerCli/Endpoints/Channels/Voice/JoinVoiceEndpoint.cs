using RaindropServer;
using RaindropServer.Channels.Voice;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Channels.Voice;

/// <summary>
/// POST /api/channels/voice/join — joins a voice channel's call and returns what the client needs to
/// connect its audio. Needs <c>connect</c>; the credential lets the microphone through only with
/// <c>speak</c> and no mute for everyone. Joining another call leaves the first.
/// </summary>
public sealed class JoinVoiceEndpoint : IAuthenticatedTypedEndpoint<JoinVoiceRequest, JoinVoiceResponse>
{
    private readonly VoiceService _voice;

    public JoinVoiceEndpoint(VoiceService voice) => _voice = voice;

    public string Route => "/api/channels/voice/join";
    public HttpMethod Method => HttpMethod.Post;

    public Task<JoinVoiceResponse> HandleAsync(
        UserSession session, JoinVoiceRequest request, CancellationToken cancellationToken) =>
        _voice.JoinAsync(session, request, cancellationToken);
}
