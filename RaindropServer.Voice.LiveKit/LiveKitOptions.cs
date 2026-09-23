using RaindropServer.Configuration;

namespace RaindropServer.Voice.LiveKit;

/// <summary>How to reach the LiveKit server that carries this Raindrop server's voice calls.</summary>
public sealed class LiveKitOptions
{
    /// <summary>
    /// Where Raindrop reaches LiveKit's HTTP API and signalling endpoint — normally a private address
    /// (<c>http://localhost:7880</c>, or <c>http://livekit:7880</c> inside Docker), never exposed to
    /// clients: they connect through Raindrop's own relay, over its pinned TLS.
    /// </summary>
    /// <remarks>
    /// Plain <c>http://</c> is accepted only for a loopback address or a single-label host name (a
    /// Docker service such as <c>livekit</c>), both of which stay on this machine or its private
    /// container network. Anything further away must be <c>https://</c>: the tokens Raindrop sends
    /// there would otherwise cross the network readable.
    /// </remarks>
    public string ApiUrl { get; set; } = "http://localhost:7880";

    /// <summary>The API key LiveKit was configured with (<c>devkey</c> in its <c>--dev</c> mode).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>The secret that goes with <see cref="ApiKey"/>. Signs every token, so keep it out of source control.</summary>
    public string ApiSecret { get; set; } = "";

    /// <summary>
    /// Accept a secret shorter than <see cref="MinimumSecretLength"/> or a well-known one such as
    /// LiveKit's <c>--dev</c> <c>secret</c>. For a throwaway server on your own machine only.
    /// </summary>
    public bool AllowWeakSecret { get; set; }

    /// <summary>The shortest secret accepted — the same floor LiveKit itself sets outside dev mode.</summary>
    public const int MinimumSecretLength = 32;

    /// <summary>The port LiveKit serves its API and signalling on unless it was told otherwise.</summary>
    public const int DefaultPort = 7880;

    /// <summary>
    /// Secrets that are not secret: LiveKit's own dev values, and the placeholders people leave in a
    /// configuration file meaning to come back to them.
    /// </summary>
    private static readonly string[] WellKnownSecrets =
        ["secret", "devkey", "changeme", "password", "livekit", "your-secret", "yoursecret", "todo"];

    /// <summary>
    /// How long a join token opens new connections. Short on purpose: a self-hosted LiveKit does not
    /// revoke a token when its holder is removed, so this bounds how long a removed member could try
    /// to reconnect. LiveKit refreshes the token of a participant who is already connected, so long
    /// calls are unaffected.
    /// </summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// What this server's rooms are called, followed by the voice channel's id. Anything else on the
    /// same LiveKit is left alone.
    /// </summary>
    public string RoomPrefix { get; set; } = "raindrop-";

    /// <summary>How long one call to LiveKit's API may take before it counts as unreachable.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <exception cref="InvalidOperationException">A required setting is missing or malformed.</exception>
    public void Validate()
    {
        var report = new ConfigurationReport().Section("voice").Section("livekit");
        Validate(report);
        report.ThrowIfInvalid();
    }

    /// <summary>
    /// Reports everything that can be told about these settings without asking LiveKit itself: that
    /// the address is one this server could use and one the tokens are safe to cross, that the
    /// credentials look like credentials rather than placeholders, and that the secret is a secret.
    /// Whether LiveKit is actually there, and actually accepts them, is
    /// <see cref="LiveKitStartupCheck"/>'s question.
    /// </summary>
    public void Validate(ConfigurationReport report)
    {
        ValidateApiUrl(report);
        ValidateCredentials(report);

        if (TokenLifetime <= TimeSpan.Zero)
            report.Error("tokenLifetime", "must be positive: a token that has already expired admits nobody.");
        else if (TokenLifetime > TimeSpan.FromHours(1))
            report.Error("tokenLifetime",
                $"is {TokenLifetime.TotalMinutes:0.##} minutes. LiveKit cannot revoke a token it has issued, so this is " +
                "how long somebody removed from the server could still reconnect to a call.",
                "Keep it to a few minutes; a participant already connected has their token refreshed anyway.");
        else if (TokenLifetime > TimeSpan.FromMinutes(15))
            report.Warn("tokenLifetime",
                $"is {TokenLifetime.TotalMinutes:0.##} minutes, which is how long a removed member could still " +
                "reconnect to a call before LiveKit stops accepting their token.");
        else if (TokenLifetime < TimeSpan.FromSeconds(30))
            report.Warn("tokenLifetime",
                $"is {TokenLifetime.TotalSeconds:0.##}s. A client whose clock is a little behind this server's would " +
                "have its token refused before it could use it.");

        if (string.IsNullOrEmpty(RoomPrefix))
            report.Error("roomPrefix",
                "must not be empty: without it this server cannot tell its own rooms from anything else sharing the " +
                "same LiveKit, and would close calls that are not its own.");
        else if (RoomPrefix.Any(char.IsWhiteSpace) || RoomPrefix.Any(character => character is '/' or '\\' or '?' or '#'))
            report.Error("roomPrefix", $"'{RoomPrefix}' contains a character that cannot appear in a room name.");

        if (RequestTimeout <= TimeSpan.Zero)
            report.Error("requestTimeout", "must be positive.");
        else if (RequestTimeout > TimeSpan.FromSeconds(60))
            report.Error("requestTimeout",
                $"is {RequestTimeout.TotalSeconds:0.##}s. A call to LiveKit holds up whoever is joining or leaving, so " +
                "an unreachable media server has to be given up on quickly.");
        else if (RequestTimeout < TimeSpan.FromSeconds(1))
            report.Warn("requestTimeout",
                $"is {RequestTimeout.TotalSeconds:0.##}s, short enough that an ordinary slow answer counts as LiveKit " +
                "being unreachable.");
    }

    /// <summary>
    /// The address: it has to parse, it has to be an API address rather than a client one, and the
    /// tokens this server sends to it must not cross a network in the clear on their way.
    /// </summary>
    private void ValidateApiUrl(ConfigurationReport report)
    {
        if (!report.Require("apiUrl", ApiUrl,
                "is required when the voice provider is 'livekit': it is where this server reaches LiveKit.",
                $"The usual value is http://localhost:{DefaultPort}, or http://livekit:{DefaultPort} inside Docker."))
            return;

        var url = ApiUrl.Trim();

        if (url != ApiUrl)
            report.Warn("apiUrl", "has whitespace around it, which is usually a copied line that brought its indentation with it.");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            report.Error("apiUrl", $"'{ApiUrl}' is not an absolute address.",
                $"Write the scheme, host and port together, as in http://localhost:{DefaultPort}.");
            return;
        }

        if (uri.Scheme is "ws" or "wss")
        {
            report.Error("apiUrl",
                $"'{ApiUrl}' is LiveKit's WebSocket address. This setting wants its HTTP API instead — the same host and " +
                "port with an http:// or https:// scheme.",
                "Clients never see either one: they reach LiveKit through this server's own relay.");
            return;
        }

        if (uri.Scheme != "http" && uri.Scheme != "https")
        {
            report.Error("apiUrl", $"must be an http:// or https:// address, not '{ApiUrl}'.");
            return;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
            report.Error("apiUrl",
                "carries a username and password. LiveKit authenticates with the API key and secret below, not with the address.");

        if (uri.AbsolutePath.Trim('/').Length > 0)
            report.Error("apiUrl",
                $"'{ApiUrl}' has a path. Give only the scheme, host and port — this server appends LiveKit's own API " +
                "paths to it, and would otherwise ask for them under yours.");

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            report.Error("apiUrl", $"'{ApiUrl}' has a query string or fragment, which LiveKit's API does not take.");

        if (uri.IsDefaultPort && uri.Port != DefaultPort)
            report.Warn("apiUrl",
                $"names no port, so this server will use {uri.Port}. LiveKit listens on {DefaultPort} unless it was " +
                $"told otherwise.", $"Add the port explicitly, as in {uri.Scheme}://{uri.Host}:{DefaultPort}.");

        // Tokens minted for this server's members travel on every one of these calls, and a token is
        // enough to join any call on this LiveKit. Plain http is only tolerable where the request
        // cannot leave the machine or its private container network.
        if (uri.Scheme == "http" && !uri.IsLoopback && uri.HostNameType == UriHostNameType.Dns && uri.Host.Contains('.'))
            report.Error("apiUrl",
                $"'{ApiUrl}' is plain http:// to a remote host, and the tokens this server sends there would cross the " +
                "network readable.",
                $"Use https://, or keep LiveKit on this machine or on a private container network " +
                $"(http://localhost:{DefaultPort}, http://livekit:{DefaultPort}).");

        if (uri.Scheme == "http" && uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 && !uri.IsLoopback)
            report.Error("apiUrl",
                $"'{ApiUrl}' is plain http:// to a network address, and the tokens this server sends there would cross " +
                "the network readable.",
                "Use https://, or reach LiveKit over loopback or a container network name.");
    }

    /// <summary>
    /// The key and the secret: present, unmangled by however they arrived, and — unless the owner has
    /// said this is a throwaway server — actually secret.
    /// </summary>
    private void ValidateCredentials(ConfigurationReport report)
    {
        var hasKey = report.Require("apiKey", ApiKey,
            "is required when the voice provider is 'livekit'.",
            "Use the same key LiveKit was configured with (LIVEKIT_API_KEY, or RAINDROP_LIVEKIT_API_KEY here).");

        var hasSecret = report.Require("apiSecret", ApiSecret,
            "is required when the voice provider is 'livekit'.",
            "Set it through RAINDROP_LIVEKIT_API_SECRET rather than writing it into raindrop.json, with the same value " +
            "LiveKit has (LIVEKIT_API_SECRET).");

        if (hasKey)
            ValidateSecretText(report, "apiKey", ApiKey);

        if (!hasSecret)
            return;

        ValidateSecretText(report, "apiSecret", ApiSecret);

        if (hasKey && ApiKey.Trim() == ApiSecret.Trim())
            report.Error("apiSecret", "is the same as apiKey. The key names this server; the secret proves it.");

        var secret = ApiSecret.Trim();

        if (AllowWeakSecret)
        {
            if (IsWeak(secret))
                report.Warn("apiSecret",
                    "is weak, and allowWeakSecret is on. Anybody who knows it can mint a token into any call on this " +
                    "server.", "Only leave this on for a throwaway server on your own machine.");

            return;
        }

        if (WellKnownSecrets.Contains(secret, StringComparer.OrdinalIgnoreCase))
            report.Error("apiSecret",
                $"is '{secret}', one of LiveKit's own dev values. Anybody who knows it can mint a token into any call.",
                "Generate one with `openssl rand -base64 48` and give the same value to LiveKit (LIVEKIT_API_SECRET).");
        else if (secret.Length < MinimumSecretLength)
            report.Error("apiSecret",
                $"is {secret.Length} characters; LiveKit's own floor outside dev mode is {MinimumSecretLength}.",
                "Generate one with `openssl rand -base64 48` and give the same value to LiveKit (LIVEKIT_API_SECRET). " +
                "Set voice.livekit.allowWeakSecret only for a throwaway local server.");
        else if (LooksUnsubstituted(secret))
            report.Error("apiSecret",
                $"is '{secret}', which reads as a placeholder rather than a secret.",
                "If it was meant to come from the environment, the variable was not set when the server started.");
        else if (DistinctCharacters(secret) < 8)
            report.Error("apiSecret",
                $"repeats only {DistinctCharacters(secret)} distinct characters, so it is long without being hard to " +
                "guess.", "Generate one with `openssl rand -base64 48`.");
    }

    /// <summary>
    /// The mistakes a credential picks up on its way in: an <c>.env</c> line that kept its quotes, a
    /// copied value that kept its whitespace, a newline that came along with it.
    /// </summary>
    private static void ValidateSecretText(ConfigurationReport report, string setting, string value)
    {
        if (value.Trim() != value)
            report.Warn(setting,
                "has whitespace around it, which this server ignores but LiveKit may not.",
                "Check for a trailing space or newline where the value was set.");

        var trimmed = value.Trim();

        if (trimmed.Length >= 2 &&
            ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
            report.Error(setting,
                $"is quoted ({trimmed}). The quotes are part of the value here, so it will not match LiveKit's.",
                "Docker Compose and .env files keep the quotes; write the value without them.");

        if (trimmed.Any(character => char.IsControl(character)))
            report.Error(setting, "contains a control character, so it cannot be the value LiveKit was given.");
    }

    /// <summary>A value nobody chose: <c>${SOMETHING}</c> or <c>&lt;your secret&gt;</c>, left as it was found.</summary>
    private static bool LooksUnsubstituted(string value) =>
        value.StartsWith('$') || value.StartsWith('<') || value.Contains("${", StringComparison.Ordinal) ||
        value.Contains("xxxx", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("your-", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("change", StringComparison.OrdinalIgnoreCase);

    private static bool IsWeak(string secret) =>
        secret.Length < MinimumSecretLength ||
        WellKnownSecrets.Contains(secret, StringComparer.OrdinalIgnoreCase) ||
        DistinctCharacters(secret) < 8;

    private static int DistinctCharacters(string value) => value.Distinct().Count();

    internal Uri ApiBase => new(ApiUrl.Trim().TrimEnd('/') + "/");
}
