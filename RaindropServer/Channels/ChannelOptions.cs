namespace RaindropServer.Channels;

/// <summary>Server-owner-tunable rules for channels and the messages inside them.</summary>
public sealed class ChannelOptions
{
    /// <summary>
    /// The longest message this server accepts, in characters. A cap keeps one client from filling
    /// the database with a single request.
    /// </summary>
    public int MaximumMessageLength { get; set; } = 4000;

    /// <summary>How many messages a history request returns when the client does not say.</summary>
    public int DefaultHistoryPageSize { get; set; } = 50;

    /// <summary>The most messages one history request may return, whatever the client asks for.</summary>
    public int MaximumHistoryPageSize { get; set; } = 200;
}
