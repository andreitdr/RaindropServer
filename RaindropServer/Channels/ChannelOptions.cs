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

    /// <summary>
    /// Whether members may still post plaintext messages, which the server can read. Off by default:
    /// every message must be end-to-end encrypted (<c>send-encrypted</c>). Turn it on only for clients
    /// that cannot encrypt yet.
    /// </summary>
    public bool AllowPlaintextMessages { get; set; }

    /// <summary>The largest end-to-end encrypted message accepted, in bytes of ciphertext.</summary>
    public int MaximumEncryptedMessageBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// How many epochs behind the channel's group an encrypted message may be. Clients keep the keys
    /// of this many past epochs, so a message sent just before a commit still decrypts after it.
    /// </summary>
    public int MaximumPastEpochs { get; set; } = 2;
}
