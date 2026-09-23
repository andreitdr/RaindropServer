using System.Net;
using System.Net.Sockets;
using RaindropServer.Channels.Voice;
using RaindropServer.Configuration;

namespace RaindropServer.Voice.LiveKit;

/// <summary>
/// Goes and looks for the LiveKit the configuration describes, before the server starts listening:
/// that its address resolves, that something is there, that it is LiveKit, that it accepts this
/// server's key and secret, and that the two machines agree roughly what time it is.
/// </summary>
/// <remarks>
/// Everything checked here is something that cannot be told by reading a setting — a secret is only
/// wrong relative to the one LiveKit was given, and a host name only fails to resolve on the machine
/// that has to resolve it. What it costs is one request; what it saves is an owner discovering all
/// this from the first member who cannot join a call.
/// <para>
/// How much it insists is <see cref="VoiceOptions.StartupCheck"/>'s to say, except for what cannot
/// resolve itself: a refused credential and an address serving something other than LiveKit are
/// mistakes in the configuration, and stay errors however patient the owner asked the server to be.
/// </para>
/// </remarks>
public sealed class LiveKitStartupCheck : IStartupCheck
{
    private readonly LiveKitOptions _options;
    private readonly VoiceOptions _voice;
    private readonly LiveKitRoomClient _rooms;

    /// <summary>How far apart the two clocks may be before tokens start to be a problem.</summary>
    private static readonly TimeSpan ToleratedClockSkew = TimeSpan.FromSeconds(30);

    public LiveKitStartupCheck(LiveKitOptions options, VoiceOptions voice, LiveKitRoomClient rooms)
    {
        _options = options;
        _voice = voice;
        _rooms = rooms;
    }

    public string Name => "voice (livekit)";

    public async Task CheckAsync(ConfigurationReport report, CancellationToken cancellationToken)
    {
        if (_voice.StartupCheck == VoiceStartupCheck.Off)
            return;

        var section = report.Section("voice").Section("livekit");
        var uri = _options.ApiBase;

        // Quoted back the way the owner wrote it, not the way it was parsed.
        var address = _options.ApiUrl.Trim();

        // A name that does not resolve never reaches the probe, and "unreachable" would be a poor
        // way of saying that this machine has never heard of the host.
        if (!await ResolvesAsync(section, uri, cancellationToken))
            return;

        var probe = await _rooms.ProbeAsync(cancellationToken);

        switch (probe.Result)
        {
            case LiveKitReachability.Reachable:
                break;

            case LiveKitReachability.CredentialsRefused:
                section.Error("apiKey",
                    $"LiveKit at {address} refused this server's API key and secret ({probe.Detail}).",
                    "They must be the same pair LiveKit itself was given — LIVEKIT_API_KEY and LIVEKIT_API_SECRET, or " +
                    "the keys file it was started with.");
                return;

            case LiveKitReachability.NotLiveKit:
                section.Error("apiUrl",
                    $"something is listening at {address}, but it does not serve LiveKit's API ({probe.Detail}).",
                    $"Check the port: LiveKit serves its API and signalling on {LiveKitOptions.DefaultPort}, not on its " +
                    "media ports (UDP 7882, TCP 7881).");
                return;

            case LiveKitReachability.TimedOut:
                Reachability(section, $"LiveKit at {address} accepted the connection but did not answer ({probe.Detail}).", null);
                return;

            default:
                Reachability(section, $"LiveKit at {address} could not be reached: {probe.Detail}.", null);
                return;
        }

        if (probe.ClockSkew is { } skew && skew.Duration() > ToleratedClockSkew)
            section.Warn("apiUrl",
                $"LiveKit's clock is {skew.Duration().TotalSeconds:0} seconds " +
                $"{(skew > TimeSpan.Zero ? "ahead of" : "behind")} this server's.",
                $"A join token is minted here and checked there, and lives {_options.TokenLifetime.TotalSeconds:0} " +
                "seconds; keep both machines on NTP.");
    }

    /// <summary>
    /// Whether the address names something this machine can find. A single-label name that does not
    /// resolve is almost always a container that is not on this server's network, which is worth
    /// saying rather than leaving as a failed connection.
    /// </summary>
    private async Task<bool> ResolvesAsync(ConfigurationReport section, Uri uri, CancellationToken cancellationToken)
    {
        if (uri.HostNameType is not UriHostNameType.Dns)
            return true;

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken);

            if (addresses.Length > 0)
                return true;
        }
        catch (SocketException)
        {
            // Reported below, the same as an empty answer.
        }

        Reachability(section,
            $"the host '{uri.Host}' does not resolve on this machine.",
            uri.Host.Contains('.')
                ? "Check the name, or give LiveKit's address directly."
                : "A single-label name like this is a container name; it only resolves from inside the same Docker " +
                  "network as LiveKit.");

        return false;
    }

    /// <summary>
    /// Records something that may simply be a media server that has not finished starting, as
    /// severely as the owner asked for it to be taken — and says what that means for this start.
    /// </summary>
    private void Reachability(ConfigurationReport section, string message, string? remedy)
    {
        var require = _voice.StartupCheck == VoiceStartupCheck.Require;

        var consequence = require
            ? "voice.startupCheck is 'require', so this server will not start until it answers; set it to 'warn' to " +
              "start anyway."
            : "Voice channels can be created, but nobody can join a call until it answers.";

        var told = remedy is null ? consequence : $"{remedy} {consequence}";

        if (require)
            section.Error("apiUrl", message, told);
        else
            section.Warn("apiUrl", message, told);
    }
}
