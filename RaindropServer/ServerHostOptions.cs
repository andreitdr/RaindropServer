using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using RaindropServer.Configuration;

namespace RaindropServer;

/// <summary>How <see cref="ServerHost"/> should listen: the addresses, and the TLS certificate.</summary>
public sealed class ServerHostOptions
{
    /// <summary>
    /// The URLs to listen on, e.g. <c>https://0.0.0.0:5001</c>. Every one must be <c>https</c>.
    /// </summary>
    public string[] Urls { get; set; } = ["https://localhost:5001"];

    /// <summary>
    /// The certificate presented to clients, which they pin. Required: clients speak gRPC to this server
    /// over TLS and trust exactly this certificate (or one its identity key vouches for), so there is no
    /// plain-HTTP mode and no reverse proxy may terminate TLS in front of it.
    /// </summary>
    public X509Certificate2? Certificate { get; set; }

    /// <summary>
    /// The TLS versions a client may negotiate. TLS 1.3 alone by default: it drops every legacy
    /// cipher and always gives forward secrecy, and every platform the client runs on speaks it. Add
    /// <see cref="SslProtocols.Tls12"/> only for an older client you cannot update.
    /// </summary>
    public SslProtocols SslProtocols { get; set; } = SslProtocols.Tls13;

    /// <summary>
    /// Whether this platform's TLS stack can serve TLS 1.3. macOS cannot: .NET serves TLS there
    /// through Apple's SecureTransport, which stops at 1.2, so a TLS 1.3-only server on a Mac would
    /// refuse every client. Linux (OpenSSL) and current Windows can.
    /// </summary>
    public static bool PlatformServesTls13 => !OperatingSystem.IsMacOS();

    /// <summary>
    /// How long a graceful shutdown may take before the remaining connections are dropped and the
    /// process exits anyway.
    /// </summary>
    /// <remarks>
    /// The host's own default is 30 seconds, which is longer than anything that stops a container is
    /// willing to wait — <c>docker stop</c> sends SIGKILL after 10 — so a server draining a
    /// long-lived connection would be killed rather than shut down. Five seconds is comfortably
    /// inside every such window while still letting in-flight requests finish.
    /// </remarks>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Throws when the addresses and the certificate contradict each other.</summary>
    /// <exception cref="InvalidOperationException">A setting is missing or contradicts another.</exception>
    public void Validate()
    {
        var report = new ConfigurationReport();
        Validate(report);
        report.ThrowIfInvalid();
    }

    /// <summary>
    /// Reports everything wrong with how this server is about to listen: the addresses themselves,
    /// whether the certificate matches what they promise, and whether that certificate is one a
    /// client will still accept tomorrow.
    /// </summary>
    public void Validate(ConfigurationReport report)
    {
        if (Urls.Length == 0)
            report.Error("urls", "is empty; the server needs at least one address to listen on.");

        var hosts = new List<string>();

        foreach (var url in Urls)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                report.Error("urls", $"'{url}' is not an https:// address.",
                    "Write the scheme, host and port together, as in https://0.0.0.0:5001.");
                continue;
            }

            if (uri.Scheme == "http")
            {
                report.Error("urls",
                    $"'{url}' listens without TLS. Clients speak gRPC to this server only over TLS, pinning its certificate.",
                    "Use https:// with the same host and port.");
                continue;
            }

            hosts.Add(uri.Host);
        }

        if (Urls.Length != Urls.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            report.Warn("urls", "lists the same address more than once.");

        if (Certificate is null)
            report.Error("tls.certificatePath",
                "names no certificate, and this server cannot listen without one.",
                "Leave certificatePath at its default to have a self-signed certificate generated on first start.");

        if ((SslProtocols & ~(SslProtocols.Tls12 | SslProtocols.Tls13)) != 0 || SslProtocols == SslProtocols.None)
            report.Error("tls.allowTls12", "leaves a TLS version enabled that this server does not serve; only 1.2 and 1.3 may be.");

        if (ShutdownTimeout <= TimeSpan.Zero)
            report.Error("shutdownTimeout", "must be positive, or an in-flight request is dropped the moment the server stops.");
        else if (ShutdownTimeout > TimeSpan.FromSeconds(10))
            report.Warn("shutdownTimeout",
                $"is {ShutdownTimeout.TotalSeconds:0.##}s. `docker stop` resorts to SIGKILL after 10, so a server still " +
                "draining after that is killed rather than shut down.");

        if (Certificate is { } certificate)
            ValidateCertificate(certificate, hosts, report.Section("tls"));
    }

    /// <summary>
    /// Reports a certificate that has expired, is not usable for TLS, or does not name the addresses
    /// this server is about to answer on.
    /// </summary>
    /// <remarks>
    /// Only the missing private key stops the server: a Raindrop client pins the fingerprint rather
    /// than validating the certificate, so one that has expired or does not cover the address it is
    /// served on still works for them. Everything else reaching the same server — a browser, a proxy,
    /// curl — validates normally and will refuse it, which is worth saying loudly and is not worth
    /// refusing to start over.
    /// </remarks>
    private static void ValidateCertificate(X509Certificate2 certificate, IReadOnlyList<string> hosts, ConfigurationReport report)
    {
        var now = DateTime.Now;

        if (certificate.NotAfter < now)
            report.Warn("certificatePath",
                $"the certificate expired on {certificate.NotAfter:u}; anything that validates it will refuse this " +
                "server, and clients that pinned its fingerprint will not.",
                "Replace the file, or delete it to have a self-signed one generated on the next start — clients that " +
                "pinned the old fingerprint follow the change through the server identity key.");
        else if (certificate.NotAfter < now.AddDays(30))
            report.Warn("certificatePath", $"the certificate expires on {certificate.NotAfter:u}.");

        if (certificate.NotBefore > now)
            report.Warn("certificatePath",
                $"the certificate is not valid until {certificate.NotBefore:u}, which is in the future.",
                "Check this machine's clock: everything that validates the certificate will refuse it until then.");

        if (!certificate.HasPrivateKey)
            report.Error("certificatePath",
                "the certificate file has no private key, so it cannot be used to serve TLS.",
                "Export the PFX with its key included.");

        foreach (var host in hosts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // A wildcard address names no host in particular; there is nothing for a certificate to
            // match it against.
            if (host is "0.0.0.0" or "[::]" or "*" or "+" or "::")
                continue;

            bool matches;
            try
            {
                matches = certificate.MatchesHostname(host, allowWildcards: true, allowCommonName: true);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (!matches)
                report.Warn("certificatePath",
                    $"the certificate does not cover '{host}', one of the addresses this server listens on.",
                    "Raindrop clients pin the fingerprint and will not mind, but anything that validates host names — a " +
                    "browser, a proxy, curl — will refuse the connection. Add it to tls.hostNames and delete the " +
                    "generated certificate to have it made again.");
        }
    }
}
