namespace RaindropServer.Voice.LiveKit;

/// <summary>How to reach the LiveKit server that carries this Raindrop server's voice calls.</summary>
public sealed class LiveKitOptions
{
    /// <summary>
    /// Where Raindrop reaches LiveKit's HTTP API and signalling endpoint — normally a private address
    /// (<c>http://localhost:7880</c>, or <c>http://livekit:7880</c> inside Docker), never exposed to
    /// clients: they connect through Raindrop's own relay, over its pinned TLS.
    /// </summary>
    public string ApiUrl { get; set; } = "http://localhost:7880";

    /// <summary>The API key LiveKit was configured with (<c>devkey</c> in its <c>--dev</c> mode).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>The secret that goes with <see cref="ApiKey"/>. Signs every token, so keep it out of source control.</summary>
    public string ApiSecret { get; set; } = "";

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
        if (!Uri.TryCreate(ApiUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new InvalidOperationException($"voice.livekit.apiUrl must be an http:// or https:// address, not '{ApiUrl}'.");

        if (string.IsNullOrWhiteSpace(ApiKey) || string.IsNullOrWhiteSpace(ApiSecret))
            throw new InvalidOperationException("voice.livekit needs both apiKey and apiSecret, matching the LiveKit server's own configuration.");

        if (TokenLifetime <= TimeSpan.Zero)
            throw new InvalidOperationException("voice.livekit token lifetime must be positive.");

        if (string.IsNullOrEmpty(RoomPrefix))
            throw new InvalidOperationException("voice.livekit room prefix must not be empty.");
    }

    internal Uri ApiBase => new(ApiUrl.TrimEnd('/') + "/");
}
