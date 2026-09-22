using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace RaindropServer.Security;

/// <summary>
/// Ed25519 signature checks, the one primitive end-to-end encryption needs from the server that .NET
/// does not ship. Users' identity keys and devices' MLS signature keys are Ed25519, so the server
/// uses this to check a device certificate before storing it and a device's answer to a
/// session-binding challenge.
/// </summary>
/// <remarks>
/// Every BouncyCastle reference in the server is in this file, so replacing the library, for
/// example with a future built-in type, touches nothing else.
/// </remarks>
public static class Ed25519Signatures
{
    public const int PublicKeyLength = 32;
    public const int SignatureLength = 64;

    /// <summary>
    /// Whether <paramref name="signature"/> is a valid Ed25519 signature by
    /// <paramref name="publicKey"/> over <paramref name="message"/>. Malformed input is simply
    /// invalid, never an exception.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != PublicKeyLength || signature.Length != SignatureLength)
            return false;

        try
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(publicKey));
            verifier.BlockUpdate(message);
            return verifier.VerifySignature(signature.ToArray());
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
