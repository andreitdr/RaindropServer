using RaindropServer;
using RaindropServer.Channels.Voice;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Channels.Voice;

/// <summary>
/// POST /api/channels/voice/state — tells everybody you muted or deafened yourself. The client stops
/// sending or playing audio itself; this is what puts the icon next to your name.
/// </summary>
public sealed class VoiceSelfStateEndpoint : IAuthenticatedTypedEndpoint<VoiceSelfStateRequest, VoiceParticipant>
{
    private readonly VoiceService _voice;

    public VoiceSelfStateEndpoint(VoiceService voice) => _voice = voice;

    public string Route => "/api/channels/voice/state";
    public HttpMethod Method => HttpMethod.Post;

    public Task<VoiceParticipant> HandleAsync(
        UserSession session, VoiceSelfStateRequest request, CancellationToken cancellationToken) =>
        _voice.UpdateSelfStateAsync(session, request, cancellationToken);
}
