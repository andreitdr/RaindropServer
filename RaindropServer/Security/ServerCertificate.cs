using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RaindropServer.Security;

/// <summary>
/// The TLS certificate a server presents. Raindrop servers are self-hosted on whatever address their
/// owner has, and most will never have a CA-issued certificate, so one is generated and kept on
/// first start.
/// </summary>
/// <remarks>
/// A self-signed certificate encrypts the connection but proves nothing about who is on the other
/// end, so clients are expected to pin it: a client already stores a per-server GUID, and stores the
/// server's <see cref="Fingerprint"/> alongside it the first time it connects, refusing to continue
/// later if it changes. That is the same trust-on-first-use model SSH uses. Point
/// <c>certificatePath</c> at a real CA-issued PFX instead and clients can verify it normally.
/// </remarks>
public static class ServerCertificate
{
    private const int KeySizeBits = 2048;
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(3650);

    /// <summary>
    /// Loads the certificate at <paramref name="path"/>, generating and saving a self-signed one if
    /// there is nothing there yet.
    /// </summary>
    /// <param name="path">Where the PKCS#12 (.pfx) file lives.</param>
    /// <param name="password">The file's password, or null when it has none.</param>
    /// <param name="hostNames">
    /// The names and IP addresses clients will reach this server by, recorded as subject alternative
    /// names so a client that validates hostnames is satisfied.
    /// </param>
    public static X509Certificate2 LoadOrCreate(string path, string? password, IReadOnlyCollection<string> hostNames)
    {
        if (File.Exists(path))
            return X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.Exportable);

        var certificate = Create(hostNames);
        var pfx = certificate.Export(X509ContentType.Pfx, password);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllBytes(path, pfx);
        RestrictToOwner(path);

        // Round-tripping through the exported bytes is what gives the loaded certificate a private
        // key usable for TLS; the freshly created object's key is not in the right form everywhere.
        return X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.Exportable);
    }

    /// <summary>
    /// The certificate's SHA-256 fingerprint, colon-separated — what a client pins and what a server
    /// owner reads out to confirm their users are talking to the right machine.
    /// </summary>
    public static string Fingerprint(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData))
            .Chunk(2)
            .Select(pair => new string(pair))
            .Aggregate((left, right) => $"{left}:{right}");

    private static X509Certificate2 Create(IReadOnlyCollection<string> hostNames)
    {
        using var key = RSA.Create(KeySizeBits);

        var request = new CertificateRequest(
            new X500DistinguishedName("CN=Raindrop Server"), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var alternativeNames = new SubjectAlternativeNameBuilder();
        var named = false;

        foreach (var host in hostNames)
        {
            if (string.IsNullOrWhiteSpace(host))
                continue;

            if (IPAddress.TryParse(host, out var address))
                alternativeNames.AddIpAddress(address);
            else
                alternativeNames.AddDnsName(host);

            named = true;
        }

        if (!named)
            alternativeNames.AddDnsName("localhost");

        request.CertificateExtensions.Add(alternativeNames.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // TLS server authentication

        var now = DateTimeOffset.UtcNow;

        // Backdated slightly so a client whose clock runs behind the server's still accepts it.
        return request.CreateSelfSigned(now.AddDays(-1), now.Add(Lifetime));
    }

    /// <summary>
    /// Takes the private key file down to owner-only. It holds the server's identity, and on a shared
    /// host the default permissions would let any local account read it.
    /// </summary>
    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
