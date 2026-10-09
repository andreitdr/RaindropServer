using RaindropServer.Configuration;

namespace RaindropServer.Channels.Voice;

/// <summary>What the server does when the media server cannot be reached as it starts.</summary>
public enum VoiceStartupCheck
{
    /// <summary>Do not go looking. The first member to join finds out instead.</summary>
    Off,

    /// <summary>
    /// Look, and say so on the console when the media server cannot be reached. The server still
    /// starts: a media sidecar and the server that uses it are often started together, and the one
    /// that wins the race should not decide whether the other runs.
    /// </summary>
    Warn,

    /// <summary>
    /// Look, and refuse to start unless the media server answers. For a server where voice not
    /// working is worse than the server not running.
    /// </summary>
    Require
}

/// <summary>Server-owner-tunable rules for voice calls.</summary>
public sealed class VoiceOptions
{
    /// <summary>
    /// The most members one voice call holds. Every participant receives everybody else's audio, so
    /// the media server's bandwidth grows with the square of this.
    /// </summary>
    public int MaximumParticipantsPerChannel { get; set; } = 25;

    /// <summary>
    /// How many members of one call may send their camera at once. Every camera goes to everybody
    /// else, and video costs an order of magnitude more than speech, so this is deliberately a much
    /// smaller number than <see cref="MaximumParticipantsPerChannel"/> — a call can be larger than
    /// the number of people in it who can be seen.
    /// </summary>
    /// <remarks>
    /// 0 switches camera video off on this server: no member is granted the camera, and
    /// <c>voice.video.v1</c> is not advertised, so clients show no camera button at all.
    /// <para>
    /// This is enforced at the media server, not only in the UI: the camera is added to a
    /// participant's publishing grant when they say their camera is on and refused when every place
    /// is taken, so a client that ignores the answer still cannot send.
    /// </para>
    /// </remarks>
    public int MaximumCameraPublishersPerChannel { get; set; } = 6;

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

    /// <summary>
    /// How much the server insists on reaching the media server before it starts. Warning by
    /// default: a media sidecar and the server that uses it are often started together, and the one
    /// that wins the race should not decide whether the other runs at all.
    /// </summary>
    /// <remarks>
    /// This governs what a transport makes of a media server it cannot reach. What it finds that
    /// cannot come right on its own — credentials the media server refuses, an address serving
    /// something else entirely — stops the server whatever this says, because no amount of waiting
    /// fixes it.
    /// </remarks>
    public VoiceStartupCheck StartupCheck { get; set; } = VoiceStartupCheck.Warn;

    /// <summary>
    /// Reports anything wrong with these rules. Settings a transport owns are validated by that
    /// transport — this is only what is true of voice calls whichever media server carries them.
    /// </summary>
    public void Validate(ConfigurationReport report)
    {
        report.Range("maximumParticipants", MaximumParticipantsPerChannel, 2, 1000, "member(s)");

        if (MaximumParticipantsPerChannel > 50)
            report.Warn("maximumParticipants",
                $"is {MaximumParticipantsPerChannel}. Every participant receives everybody else's audio, so a full " +
                "call costs the media server roughly the square of this in bandwidth.");

        report.Range("maximumCameraPublishers", MaximumCameraPublishersPerChannel, 0, 50, "publisher(s)");

        // A cap above the call's own capacity is not wrong, only unreachable — and an owner who wrote
        // one probably meant the call to hold that many people.
        if (MaximumCameraPublishersPerChannel > MaximumParticipantsPerChannel)
            report.Warn("maximumCameraPublishers",
                $"is {MaximumCameraPublishersPerChannel}, more than the {MaximumParticipantsPerChannel} members a " +
                "call holds, so it can never be reached.");

        if (MaximumCameraPublishersPerChannel > 12)
            report.Warn("maximumCameraPublishers",
                $"is {MaximumCameraPublishersPerChannel}. Every camera is sent to everybody else in the call, so " +
                $"that many cameras is up to {MaximumCameraPublishersPerChannel * (MaximumParticipantsPerChannel - 1)} " +
                "video streams out of the media server at once — far more than the audio costs.");

        report.Range("joinGrace", JoinGrace, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10));
        report.Range("reconcileInterval", ReconcileInterval, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));

        if (JoinGrace < TimeSpan.FromSeconds(5))
            report.Warn("joinGrace",
                $"is {JoinGrace.TotalSeconds:0.##}s. A client on a slow network can take longer than that to reach the " +
                "media server, and would be dropped from the call it had just been admitted to.");

        // The grace period is only ever noticed on a reconcile pass, so an interval longer than the
        // grace quietly rounds it up — the setting would not mean what it says.
        if (ReconcileInterval > JoinGrace)
            report.Warn("reconcileInterval",
                $"is {ReconcileInterval.TotalSeconds:0.##}s, longer than the {JoinGrace.TotalSeconds:0.##}s join grace. " +
                "The grace is only checked once per pass, so it effectively lasts as long as this interval.");
    }
}
