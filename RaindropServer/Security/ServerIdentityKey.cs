using System.Security.Cryptography;
using System.Text;

namespace RaindropServer.Security;

/// <summary>
/// The server's long-lived signing key: the identity a client pins, as opposed to the TLS certificate,
/// which may change.
/// </summary>
/// <remarks>
/// A certificate pinned on first use leaves a server owner no way to replace it — renewing it,
/// moving to a CA-issued one, or recovering from a leaked key — without every client refusing to
/// connect, which trains users to click through exactly the warning that protects them. So the
/// server also keeps this key, and signs the fingerprint of whichever certificate it presents
/// (<see cref="SignTlsBinding"/>). A client that pinned the key accepts a new certificate when, and
/// only when, the old identity vouches for it.
///
/// ECDSA P-256 because it is built into .NET on every platform; the wire forms are deliberately the
/// simplest ones — an uncompressed point and a fixed-width <c>r‖s</c> signature — so a client needs
/// no ASN.1 to check them.
/// </remarks>
public sealed class ServerIdentityKey : IDisposable
{
    /// <summary>
    /// Prefixed to what is signed, so a signature made for this purpose can never be passed off as
    /// one made for another.
    /// </summary>
    public const string TlsBindingContext = "raindrop/tls-binding/v1\n";

    private readonly ECDsa _key;

    private ServerIdentityKey(ECDsa key)
    {
        _key = key;
        var point = key.ExportParameters(includePrivateParameters: false).Q;
        PublicKey = [0x04, .. point.X!, .. point.Y!];
    }

    /// <summary>The public key as an uncompressed P-256 point (<c>0x04‖X‖Y</c>, 65 bytes).</summary>
    public byte[] PublicKey { get; }

    /// <summary>
    /// Loads the key at <paramref name="path"/>, generating and saving one if there is nothing there
    /// yet. Losing this file means every client sees a new identity, so it belongs in backups
    /// alongside the certificate.
    /// </summary>
    public static ServerIdentityKey LoadOrCreate(string path)
    {
        var key = ECDsa.Create();

        if (File.Exists(path))
        {
            key.ImportFromPem(File.ReadAllText(path));

            if (key.KeySize != 256)
                throw new CryptographicException($"'{path}' is not a P-256 key.");

            return new ServerIdentityKey(key);
        }

        key.GenerateKey(ECCurve.NamedCurves.nistP256);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, key.ExportPkcs8PrivateKeyPem());
        FilePermissions.RestrictToOwner(path);

        return new ServerIdentityKey(key);
    }

    /// <summary>
    /// Signs the certificate <paramref name="fingerprint"/> this server presents, in any of the forms
    /// it is written (colon-separated or not, either case).
    /// </summary>
    /// <returns>A 64-byte <c>r‖s</c> ECDSA-SHA256 signature.</returns>
    public byte[] SignTlsBinding(string fingerprint) =>
        _key.SignData(TlsBindingMessage(fingerprint), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    /// <summary>
    /// The exact bytes <see cref="SignTlsBinding"/> signs: the context, then the fingerprint as
    /// lower-case hex with no separators.
    /// </summary>
    public static byte[] TlsBindingMessage(string fingerprint)
    {
        var normalized = new string(fingerprint.Where(Uri.IsHexDigit).Select(char.ToLowerInvariant).ToArray());
        return Encoding.UTF8.GetBytes(TlsBindingContext + normalized);
    }

    /// <summary>Checks a signature made by <see cref="SignTlsBinding"/> against a public key.</summary>
    public static bool VerifyTlsBinding(byte[] publicKey, string fingerprint, byte[] signature)
    {
        if (publicKey is not [0x04, ..] || publicKey.Length != 65)
            return false;

        using var key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..] }
        });

        return key.VerifyData(TlsBindingMessage(fingerprint), signature, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public void Dispose() => _key.Dispose();
}
