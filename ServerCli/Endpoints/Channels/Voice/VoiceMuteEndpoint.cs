using RaindropServer;
using RaindropServer.Channels.Voice;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Channels.Voice;

/// <summary>
/// POST /api/channels/voice/mute — mutes a member in every voice call on this server, for everyone,
/// or lifts that mute. Needs <c>mute_members</c> server-wide. Stored, so it survives a restart.
/// </summary>
public sealed class VoiceMuteEndpoint : IAuthenticatedTypedEndpoint<VoiceMuteRequest, VoiceMuteResponse>
{
    private readonly VoiceService _voice;

    public VoiceMuteEndpoint(VoiceService voice) => _voice = voice;

    public string Route => "/api/channels/voice/mute";
    public HttpMethod Method => HttpMethod.Post;

    public Task<VoiceMuteResponse> HandleAsync(
        UserSession session, VoiceMuteRequest request, CancellationToken cancellationToken) =>
        _voice.SetServerMuteAsync(session, request, cancellationToken);
}
