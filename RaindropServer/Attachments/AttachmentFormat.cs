namespace RaindropServer.Attachments;

/// <summary>
/// What the server knows about the bytes of an attachment without being able to read them: the
/// client encrypts every file in the <c>raindrop/attachment/v1</c> format defined by the Rust core
/// (<c>RaindropCrypto/crates/raindrop_crypto/src/attachment.rs</c>), whose ciphertext length fixes the
/// plaintext length exactly. That is how a size limit is enforced on a file this server cannot open.
/// </summary>
/// <remarks>
/// A version byte and a 19-byte nonce prefix, then the plaintext in 64 KiB segments, each followed by a
/// 16-byte tag; an empty file is one empty segment. Change this only together with the Rust core, under
/// a new version: see <c>RaindropCrypto/docs/wire-contracts.md</c>.
/// </remarks>
public static class AttachmentFormat
{
    /// <summary>Plaintext bytes per segment.</summary>
    public const long SegmentLength = 64 * 1024;

    /// <summary>Bytes the AEAD adds to each segment.</summary>
    public const long TagLength = 16;

    /// <summary>The version byte and the nonce prefix.</summary>
    public const long HeaderLength = 1 + 19;

    /// <summary>How many bytes of ciphertext a file of <paramref name="plaintext"/> bytes becomes.</summary>
    public static long CiphertextLength(long plaintext)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(plaintext);

        var segments = Math.Max(1, (plaintext + SegmentLength - 1) / SegmentLength);
        return HeaderLength + plaintext + TagLength * segments;
    }

    /// <summary>
    /// The plaintext length behind <paramref name="ciphertext"/> bytes of ciphertext, or null when no
    /// file encrypts to that length — which is how a made-up size is refused before anything is stored.
    /// </summary>
    public static long? PlaintextLength(long ciphertext)
    {
        var body = ciphertext - HeaderLength;

        if (body < TagLength)
            return null;

        var sealedSegment = SegmentLength + TagLength;
        var segments = Math.Max(1, (body + sealedSegment - 1) / sealedSegment);
        var plaintext = body - TagLength * segments;

        return plaintext >= 0 && CiphertextLength(plaintext) == ciphertext ? plaintext : null;
    }
}
