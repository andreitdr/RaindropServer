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
    /// How many members of one call may share their screen at once. A share is sent at up to 1080p,
    /// and costs several cameras' worth of bandwidth for everybody who watches it, so this is smaller
    /// still than <see cref="MaximumCameraPublishersPerChannel"/>.
    /// </summary>
    /// <remarks>
    /// 0 switches screen sharing off on this server: nobody is granted a share, and
    /// <c>voice.screen.v1</c> is not advertised, so clients show no share button. Enforced at the
    /// media server the same way as the camera cap.
    /// </remarks>
    public int MaximumScreenSharesPerChannel { get; set; } = 2;

    /// <summary>
    /// The most lines a shared screen may be sent at: 480, 720 or 1080. A sharer picks their own
    /// maximum up to this, and a client fits the picture inside the box it names.
    /// </summary>
    /// <remarks>
    /// <b>Honoured by clients, not enforced here.</b> A call's media is end-to-end encrypted and goes
    /// through the media server, never through Raindrop, so there is no frame for this server to
    /// measure and nothing it could refuse. It is advertised in the voice status and a Raindrop client
    /// holds its share to it — the same kind of promise the 1080p ceiling always was. What it buys an
    /// owner is bandwidth on the media server: see <see cref="ScreenShareBitrate"/>.
    /// </remarks>
    public int MaximumScreenShareHeight { get; set; } = 1080;

    /// <summary>
    /// The most frames a second a shared screen may be sent at: 15, 24, 30 or 60. Honoured by clients,
    /// like <see cref="MaximumScreenShareHeight"/>.
    /// </summary>
    public int MaximumScreenShareFrameRate { get; set; } = 60;

    /// <summary>The heights a share's ceiling can be, smallest first.</summary>
    public static readonly IReadOnlyList<int> ScreenShareHeights = [480, 720, 1080];

    /// <summary>The frame rates a share's ceiling can be, slowest first.</summary>
    public static readonly IReadOnlyList<int> ScreenShareFrameRates = [15, 24, 30, 60];

    /// <summary>
    /// The most one share's largest version costs at a given ceiling, in kilobits a second — what a
    /// client caps its encoder at, kept here so the warnings an owner reads are the client's own
    /// numbers. A watcher receives at most this; the sharer sends this plus about half as much again
    /// for the smaller versions sent alongside it for watchers on slower connections.
    /// </summary>
    public static int ScreenShareBitrate(int height, int frameRate)
    {
        int[] row = height switch
        {
            <= 480 => [500, 700, 800, 1200],
            <= 720 => [1200, 1600, 2000, 3000],
            _ => [2500, 3200, 4000, 6000]
        };

        return frameRate switch
        {
            <= 15 => row[0],
            <= 24 => row[1],
            <= 30 => row[2],
            _ => row[3]
        };
    }

    /// <summary>
    /// How long a member may hold a place in a call after joining before the media server has seen
    /// them connect. A client that got its token and then crashed, or could not reach the media server
    /// at all, is dropped once this passes rather than showing as present forever.
    /// </summary>
    public TimeSpan JoinGrace { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long a member's media signalling connection may stay closed before they are taken out of
    /// the call — see <see cref="IVoiceSignalMonitor"/>. A client that crashed or was killed leaves
    /// this long after its sockets die. Long enough for a client recovering from a network blip to
    /// open a new connection, which cancels the removal.
    /// </summary>
    public TimeSpan SignalLossGrace { get; set; } = TimeSpan.FromSeconds(5);

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

        report.Range("maximumScreenShares", MaximumScreenSharesPerChannel, 0, 10, "share(s)");

        if (MaximumScreenSharesPerChannel > MaximumParticipantsPerChannel)
            report.Warn("maximumScreenShares",
                $"is {MaximumScreenSharesPerChannel}, more than the {MaximumParticipantsPerChannel} members a " +
                "call holds, so it can never be reached.");

        // A closed set rather than a range: a client sends a share in a ladder of fixed sizes and
        // rates, and a ceiling between two of them would be one no client can aim at.
        var heightKnown = ScreenShareHeights.Contains(MaximumScreenShareHeight);
        if (!heightKnown)
            report.Error("maximumScreenShareHeight",
                $"is {MaximumScreenShareHeight}, which is not a size a share can be capped at.",
                $"Use one of {string.Join(", ", ScreenShareHeights)}.");

        var rateKnown = ScreenShareFrameRates.Contains(MaximumScreenShareFrameRate);
        if (!rateKnown)
            report.Error("maximumScreenShareFrameRate",
                $"is {MaximumScreenShareFrameRate}, which is not a frame rate a share can be capped at.",
                $"Use one of {string.Join(", ", ScreenShareFrameRates)}.");

        if (MaximumScreenSharesPerChannel > 3 && heightKnown && rateKnown)
        {
            var megabits = ScreenShareBitrate(MaximumScreenShareHeight, MaximumScreenShareFrameRate) / 1000.0;
            report.Warn("maximumScreenShares",
                $"is {MaximumScreenSharesPerChannel}. At the {MaximumScreenShareHeight}p, " +
                $"{MaximumScreenShareFrameRate} frames a second this server allows, each share is up to about " +
                $"{megabits * 1.5:0.#} Mbit/s from its sharer and up to {megabits:0.#} Mbit/s to every member " +
                "watching it, so that many at once can outweigh the rest of the call.",
                "Allow fewer shares at once, or lower maximumScreenShareHeight or maximumScreenShareFrameRate.");
        }

        report.Range("joinGrace", JoinGrace, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10));
        report.Range("reconcileInterval", ReconcileInterval, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));
        report.Range("signalLossGrace", SignalLossGrace, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2));

        if (SignalLossGrace < TimeSpan.FromSeconds(2))
            report.Warn("signalLossGrace",
                $"is {SignalLossGrace.TotalSeconds:0.##}s. A client resuming after a short network drop needs a " +
                "moment to open a new connection, and would be dropped from its call first.");

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
