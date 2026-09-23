using RaindropServer.Configuration;

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

    /// <summary>Reports message and history rules that are impossible, or that contradict each other.</summary>
    public void Validate(ConfigurationReport report)
    {
        report.Range("maximumMessageLength", MaximumMessageLength, 1, 1_000_000, "character(s)");

        if (MaximumMessageLength > 64_000)
            report.Warn("maximumMessageLength",
                $"is {MaximumMessageLength:N0} characters. One request can then store that much, and every client that " +
                "reads the channel has to carry it.");

        report.Range("maximumHistoryPageSize", MaximumHistoryPageSize, 1, 10_000, "message(s)");
        report.Range("defaultHistoryPageSize", DefaultHistoryPageSize, 1, 10_000, "message(s)");

        if (DefaultHistoryPageSize > MaximumHistoryPageSize)
            report.Error("defaultHistoryPageSize",
                $"is {DefaultHistoryPageSize}, more than the {MaximumHistoryPageSize} a request may ever return, so a " +
                "client that does not ask for a page size would be refused its own default.");

        if (MaximumHistoryPageSize > 1000)
            report.Warn("maximumHistoryPageSize",
                $"is {MaximumHistoryPageSize:N0}; one history request can then ask the database for that many rows at once.");

        if (MaximumEncryptedMessageBytes <= 0)
            report.Error("maximumEncryptedMessageBytes", "must be positive, or no encrypted message can be posted at all.");
        else if (MaximumEncryptedMessageBytes <= MaximumMessageLength)
            report.Warn("maximumEncryptedMessageBytes",
                $"is {MaximumEncryptedMessageBytes:N0} bytes, no more than the {MaximumMessageLength:N0} characters a " +
                "plaintext message may hold. Ciphertext is longer than its plaintext, so the longest messages could be " +
                "sent in the clear but not encrypted.");

        if (MaximumPastEpochs < 0)
            report.Error("maximumPastEpochs", "cannot be negative.");
        else if (MaximumPastEpochs > 10)
            report.Warn("maximumPastEpochs",
                $"is {MaximumPastEpochs}: a message encrypted to a group key that many commits old is still accepted, " +
                "which is that many more chances for somebody removed from the channel to still be able to post.");

        if (AllowPlaintextMessages)
            report.Warn("allowPlaintextMessages",
                "is on, so members may post messages this server can read and store in the clear.",
                "Only for clients that cannot encrypt yet; turn it off once they can.");
    }
}
