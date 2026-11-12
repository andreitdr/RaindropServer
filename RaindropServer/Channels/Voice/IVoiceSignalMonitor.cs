namespace RaindropServer.Channels.Voice;

/// <summary>
/// Told when a member's media signalling connection opens and closes — by an
/// <see cref="IVoiceSignalRelay"/>, which is the one place that sees it.
/// </summary>
/// <remarks>
/// This is how a client that crashed, was killed or lost power leaves the call within seconds instead
/// of when the media server and the reconciler eventually give up on it: its process cannot say
/// goodbye, but the operating system closes its sockets, and the relay notices. <see cref="VoiceService"/>
/// implements it; a member whose last signalling connection stays closed for
/// <see cref="VoiceOptions.SignalLossGrace"/> is taken out of the call (<c>disconnected</c>).
/// <para>
/// A transport without a relay never calls this, and the reconciler alone decides, as before.
/// </para>
/// </remarks>
public interface IVoiceSignalMonitor
{
    /// <summary>A signalling connection for <paramref name="userId"/> in that call was accepted.</summary>
    void SignalOpened(Guid channelId, Guid userId);

    /// <summary>One of their signalling connections ended, however it ended.</summary>
    void SignalClosed(Guid channelId, Guid userId);
}
