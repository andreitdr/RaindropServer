namespace RaindropServer.Channels;

/// <summary>
/// The kind of channel, which decides what happens inside it. The shared code in
/// <see cref="ChannelService"/> never branches on this — it dispatches to the
/// <see cref="IChannelModule"/> registered for the type — so adding a kind means adding a module,
/// not editing the code that already works.
/// </summary>
public enum ChannelType
{
    /// <summary>Messages, in chronological order. See <c>Channels/Text/</c>.</summary>
    Text = 0,

    /// <summary>
    /// A call: voice, and later camera and screen sharing. Its code lives in <c>Channels/Voice/</c>;
    /// the audio itself is carried by whichever <c>IVoiceTransport</c> the server registered.
    /// </summary>
    Voice = 1
}

/// <summary>The wire names for <see cref="ChannelType"/>, which is how clients name a type.</summary>
public static class ChannelTypeNames
{
    public const string Text = "text";
    public const string Voice = "voice";

    public static string Of(ChannelType type) => type switch
    {
        ChannelType.Text => Text,
        ChannelType.Voice => Voice,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown channel type.")
    };

    /// <summary>Reads a type name sent by a client, rejecting anything this server has never heard of.</summary>
    /// <exception cref="ApiException">The name is missing or not a channel type.</exception>
    public static ChannelType Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        Text => ChannelType.Text,
        Voice => ChannelType.Voice,
        _ => throw ApiException.BadRequest("invalid_channel_type",
            $"A channel type is required, and must be one of: {Text}, {Voice}.")
    };
}
