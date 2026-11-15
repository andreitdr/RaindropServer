using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RaindropServer.Security;

/// <summary>
/// Time-based one-time passwords as authenticator apps compute them: RFC 6238 with HMAC-SHA1, a
/// 30-second step and six digits. Those parameters are the ones every authenticator app defaults to
/// and many cannot change, which is the only reason SHA-1 is here.
/// </summary>
public static class Totp
{
    public const int StepSeconds = 30;
    public const int Digits = 6;

    /// <summary>The step a moment falls in.</summary>
    public static long StepAt(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds() / StepSeconds;

    /// <summary>The six digits for <paramref name="step"/>.</summary>
    public static string Code(ReadOnlySpan<byte> secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);

        Span<byte> mac = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(secret, counter, mac);

        var offset = mac[^1] & 0x0f;
        var binary = BinaryPrimitives.ReadUInt32BigEndian(mac[offset..]) & 0x7fff_ffff;
        return (binary % 1_000_000).ToString("D6");
    }

    /// <summary>
    /// The step <paramref name="code"/> is right for, looking one step either side of now to allow for
    /// a clock that is a little off, or null when it is right for none of them.
    /// </summary>
    public static long? MatchingStep(ReadOnlySpan<byte> secret, string code, DateTime utcNow)
    {
        if (code.Length != Digits)
            return null;

        var now = StepAt(utcNow);
        long? matched = null;

        // Every candidate is compared, in constant time each, so how long this takes says nothing
        // about which one matched.
        for (var step = now - 1; step <= now + 1; step++)
        {
            var expected = Code(secret, step);
            if (CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(expected), System.Text.Encoding.ASCII.GetBytes(code)))
            {
                matched ??= step;
            }
        }

        return matched;
    }
}
