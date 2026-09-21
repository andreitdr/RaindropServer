namespace RaindropServer.Channels.Voice;

/// <summary>Server-owner-tunable rules for voice calls.</summary>
public sealed class VoiceOptions
{
    /// <summary>
    /// The most members one voice call holds. Every participant receives everybody else's audio, so
    /// the media server's bandwidth grows with the square of this.
    /// </summary>
    public int MaximumParticipantsPerChannel { get; set; } = 25;

    /// <summary>
    /// How long a member may hold a place in a call after joining before the media server has seen
    /// them connect. A client that got its token and then crashed, or could not reach the media server
    /// at all, is dropped once this passes rather than showing as present forever.
    /// </summary>
    public TimeSpan JoinGrace { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How often Raindrop's record of every call is compared with the media server's — how quickly a
    /// client that vanished without leaving drops out, and how quickly anybody the media server has
    /// but Raindrop did not admit is removed.
    /// </summary>
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromSeconds(5);
}
