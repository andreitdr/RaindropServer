using Microsoft.AspNetCore.Http;

namespace RaindropServer.Channels.Voice;

/// <summary>
/// Whatever actually carries the media of a voice call. Raindrop decides who may join, speak, show
/// their camera, share their screen and moderate; the transport only moves media, and is told the
/// outcome of those decisions.
/// </summary>
/// <remarks>
/// The seam a media server plugs in behind. The built-in implementation drives a LiveKit sidecar
/// (the <c>RaindropServer.Voice.LiveKit</c> assembly); without one registered,
/// <see cref="UnavailableVoiceTransport"/> refuses every join. Nothing in <see cref="VoiceService"/>
/// knows which one it is talking to — the join response carries <see cref="Name"/>, and a client
/// picks the matching engine by it.
/// <para>
/// Every call is keyed by the voice channel's id; how a transport names its rooms is its own
/// business. Calls that reach the media server throw <see cref="VoiceTransportException"/> when it
/// cannot be reached, which the voice subsystem treats as "try again later", never as a crash.
/// </para>
/// </remarks>
public interface IVoiceTransport
{
    /// <summary>
    /// The name a client matches on to pick its engine, e.g. <c>livekit</c>. An open string: a
    /// client that does not recognise it reports voice as unsupported rather than guessing.
    /// </summary>
    string Name { get; }

    /// <summary>False when no media server is configured, and joining is refused outright.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Mints what a client needs to connect to the call — a short-lived credential scoped to exactly
    /// what <paramref name="grant"/> allows. Does not admit anybody by itself: the caller records the
    /// participant first, and the transport's own enforcement checks that record.
    /// </summary>
    Task<VoiceConnection> CreateConnectionAsync(VoiceGrant grant, CancellationToken cancellationToken);

    /// <summary>
    /// Changes what a participant who is already connected may send, without disconnecting them —
    /// how a server mute, a lost permission, a camera turned on and off, or a screen share started
    /// and stopped takes effect mid-call.
    /// </summary>
    /// <remarks>
    /// One call carrying the whole grant rather than one per source, because a transport that
    /// replaces a participant's whole permission set (LiveKit does) would have several calls racing
    /// to describe the same participant. A transport withdraws any track whose source the new grant
    /// no longer allows.
    /// </remarks>
    Task SetPublishAsync(
        Guid channelId,
        Guid userId,
        VoicePublishGrant grant,
        CancellationToken cancellationToken);

    /// <summary>Removes one participant from a call. Succeeds quietly when they are not in it.</summary>
    Task DisconnectAsync(Guid channelId, Guid userId, CancellationToken cancellationToken);

    /// <summary>Ends a call for everybody in it. Succeeds quietly when there is no call.</summary>
    Task CloseCallAsync(Guid channelId, CancellationToken cancellationToken);

    /// <summary>
    /// Every call the media server is carrying for this Raindrop server, and who is connected to
    /// each — the ground truth the reconciler compares Raindrop's own record against.
    /// </summary>
    Task<IReadOnlyList<TransportCall>> ListCallsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Optionally implemented by an <see cref="IVoiceTransport"/> whose clients open their media
/// connection through this server rather than straight to the media server. Raindrop then serves
/// <c>/api/channels/voice/signal/{path}</c> (<see cref="VoiceSignalRoutes"/>) by handing the request here,
/// so that connection rides the same pinned TLS certificate as everything else.
/// </summary>
public interface IVoiceSignalRelay
{
    /// <summary>
    /// Serves one request under the relay path. <paramref name="path"/> is what follows
    /// <c>/api/channels/voice/signal/</c>, and a relay answers 404 for anything it does not forward.
    /// </summary>
    Task RelayAsync(HttpContext context, string path, CancellationToken cancellationToken);
}

/// <summary>What a participant is allowed to do in the call they are joining.</summary>
/// <param name="ChannelId">The voice channel.</param>
/// <param name="UserId">The member; also their identity inside the call.</param>
/// <param name="DisplayName">
/// Their username at the time of joining. Clients should not rely on it staying current — a member
/// can be renamed mid-call — and resolve names from the UUID instead.
/// </param>
/// <param name="Publish">
/// What the transport should accept from them. At join this is the microphone at most, whatever their
/// permissions say: the camera and a screen share are granted only once a member says they have
/// started one, which is also where the server's caps on simultaneous cameras and shares apply.
/// </param>
public sealed record VoiceGrant(
    Guid ChannelId,
    Guid UserId,
    string DisplayName,
    VoicePublishGrant Publish);

/// <summary>
/// Which of a participant's media sources the transport should accept. Each is decided by Raindrop;
/// the transport only enforces the answer.
/// </summary>
/// <param name="Microphone">Their voice.</param>
/// <param name="Camera">Their camera.</param>
/// <param name="Screen">The picture of the screen or window they are sharing.</param>
/// <param name="ScreenAudio">
/// The shared window's own sound. Only ever with <paramref name="Screen"/>, and never for a whole
/// screen.
/// </param>
public readonly record struct VoicePublishGrant(bool Microphone, bool Camera, bool Screen, bool ScreenAudio)
{
    /// <summary>Nothing at all: a participant who may only listen.</summary>
    public static VoicePublishGrant None => default;

    /// <summary>Whether any source is allowed.</summary>
    public bool Any => Microphone || Camera || Screen || ScreenAudio;
}

/// <summary>What a client needs to connect its media to the call.</summary>
/// <param name="Transport">The transport's <see cref="IVoiceTransport.Name"/>.</param>
/// <param name="Url">
/// An absolute address to connect to, or null when the client should connect through this server at
/// <paramref name="Path"/> instead.
/// </param>
/// <param name="Path">A path on this server to connect to, used when <paramref name="Url"/> is null.</param>
/// <param name="Token">The transport's credential. Short-lived; a client joins again for a fresh one.</param>
/// <param name="ExpiresAtUtc">When <paramref name="Token"/> stops being accepted for a new connection.</param>
public sealed record VoiceConnection(string Transport, string? Url, string? Path, string Token, DateTime ExpiresAtUtc);

/// <summary>One call as the media server sees it.</summary>
public sealed record TransportCall(Guid ChannelId, IReadOnlyList<TransportParticipant> Participants);

/// <summary>One connected participant as the media server sees them.</summary>
/// <param name="UserId">Their identity in the call.</param>
/// <param name="Publish">What the media server currently accepts from them.</param>
public sealed record TransportParticipant(Guid UserId, VoicePublishGrant Publish);

/// <summary>The media server could not be reached, or refused a request it should have accepted.</summary>
public sealed class VoiceTransportException : Exception
{
    public VoiceTransportException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
