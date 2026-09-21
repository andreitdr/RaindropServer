using System.Buffers.Text;
using System.Text;

namespace RaindropServer.Channels.Text;

/// <summary>
/// Where a client got to when paging back through a channel's history. Handed out as an opaque
/// string so the client only ever passes it back, and the server can change what it encodes.
/// </summary>
/// <remarks>
/// It is a pair, not just a timestamp: two messages can share a timestamp, and the id breaks the tie
/// so that paging can never show one message twice or skip one entirely.
/// </remarks>
/// <param name="SentAtUtc">The timestamp of the message the last page ended on.</param>
/// <param name="MessageId">The id of that message.</param>
public readonly record struct MessageCursor(DateTime SentAtUtc, Guid MessageId)
{
    /// <summary>The cursor pointing just before <paramref name="message"/>.</summary>
    public static MessageCursor Of(TextMessageRecord message) => new(message.SentAtUtc, message.MessageId);

    /// <summary>Encodes this cursor as the opaque token a client receives.</summary>
    public string Encode() =>
        Base64Url.EncodeToString(Encoding.ASCII.GetBytes($"{SentAtUtc.Ticks}:{MessageId:D}"));

    /// <summary>
    /// Decodes a token a client sent back. Null passes through as null — that is simply the first
    /// page, the newest messages in the channel.
    /// </summary>
    /// <exception cref="ApiException">The token is not one this server handed out.</exception>
    public static MessageCursor? Decode(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        try
        {
            var decoded = Encoding.ASCII.GetString(Base64Url.DecodeFromChars(token));
            var separator = decoded.IndexOf(':');

            if (separator > 0
                && long.TryParse(decoded[..separator], out var ticks)
                && Guid.TryParseExact(decoded[(separator + 1)..], "D", out var messageId)
                && ticks is >= 0 and <= 3_155_378_975_999_999_999)
            {
                return new MessageCursor(new DateTime(ticks, DateTimeKind.Utc), messageId);
            }
        }
        catch (FormatException)
        {
            // Falls through to the same error as a well-formed token carrying nonsense.
        }

        throw ApiException.BadRequest("invalid_cursor",
            "That is not a history cursor this server issued. Ask for the newest page instead.");
    }
}
