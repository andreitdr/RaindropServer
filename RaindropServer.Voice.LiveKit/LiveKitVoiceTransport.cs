using RaindropServer.Channels.Voice;

namespace RaindropServer.Voice.LiveKit;

/// <summary>
/// Voice calls carried by a LiveKit server. One LiveKit room per voice channel, named by
/// <see cref="LiveKitJwt.RoomFor"/>; each participant's identity is their Raindrop UUID.
/// </summary>
/// <remarks>
/// Clients never talk to LiveKit's HTTP port directly: the join response points them at
/// <see cref="SignalPath"/> on this server, which <see cref="LiveKitSignalRelay"/> forwards. Only
/// LiveKit's media ports (UDP, and the TCP fallback) have to be reachable from outside.
/// </remarks>
public sealed class LiveKitVoiceTransport : IVoiceTransport
{
    /// <summary>What clients match on to pick their LiveKit engine.</summary>
    public const string TransportName = "livekit";

    /// <summary>Where clients open LiveKit's signalling connection on this server.</summary>
    public const string SignalPath = "/api/channels/voice/signal";

    private readonly LiveKitJwt _jwt;
    private readonly LiveKitRoomClient _rooms;

    public LiveKitVoiceTransport(LiveKitJwt jwt, LiveKitRoomClient rooms)
    {
        _jwt = jwt;
        _rooms = rooms;
    }

    public string Name => TransportName;

    public bool IsAvailable => true;

    public Task<VoiceConnection> CreateConnectionAsync(VoiceGrant grant, CancellationToken cancellationToken)
    {
        var (token, expiresAtUtc) = _jwt.CreateParticipantToken(grant);
        return Task.FromResult(new VoiceConnection(Name, null, SignalPath, token, expiresAtUtc));
    }

    public Task SetCanSpeakAsync(Guid channelId, Guid userId, bool canSpeak, CancellationToken cancellationToken) =>
        _rooms.UpdatePermissionAsync(_jwt.RoomFor(channelId), userId.ToString("D"), canSpeak, cancellationToken);

    public Task DisconnectAsync(Guid channelId, Guid userId, CancellationToken cancellationToken) =>
        _rooms.RemoveParticipantAsync(_jwt.RoomFor(channelId), userId.ToString("D"), cancellationToken);

    public Task CloseCallAsync(Guid channelId, CancellationToken cancellationToken) =>
        _rooms.DeleteRoomAsync(_jwt.RoomFor(channelId), cancellationToken);

    public async Task<IReadOnlyList<TransportCall>> ListCallsAsync(CancellationToken cancellationToken)
    {
        var calls = new List<TransportCall>();

        foreach (var room in await _rooms.ListRoomsAsync(cancellationToken))
        {
            if (_jwt.ChannelFor(room) is not { } channelId)
                continue;

            var participants = new List<TransportParticipant>();

            foreach (var participant in await _rooms.ListParticipantsAsync(room, cancellationToken))
            {
                if (Guid.TryParse(participant.Identity, out var userId))
                {
                    participants.Add(new TransportParticipant(userId, participant.CanPublish));
                    continue;
                }

                // Not a Raindrop member at all — nobody Raindrop could have admitted, so nobody the
                // reconciler could name to remove. It goes here instead.
                await _rooms.RemoveParticipantAsync(room, participant.Identity, cancellationToken);
            }

            calls.Add(new TransportCall(channelId, participants));
        }

        return calls;
    }
}
