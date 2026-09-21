using System.Security.Cryptography.X509Certificates;

namespace RaindropServer;

/// <summary>How <see cref="ServerHost"/> should listen: the addresses, and the TLS certificate.</summary>
public sealed class ServerHostOptions
{
    /// <summary>
    /// The URLs to listen on, e.g. <c>https://0.0.0.0:5001</c>. An <c>https</c> URL requires
    /// <see cref="Certificate"/>.
    /// </summary>
    public string[] Urls { get; set; } = ["https://localhost:5001"];

    /// <summary>
    /// The certificate presented to clients. Leave null only when the server is behind a reverse
    /// proxy that terminates TLS, or for local development over plain HTTP.
    /// </summary>
    public X509Certificate2? Certificate { get; set; }

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
    public void Validate()
    {
        if (Urls.Length == 0)
            throw new InvalidOperationException("At least one URL to listen on is required.");

        var https = Urls.Any(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

        if (https && Certificate is null)
            throw new InvalidOperationException(
                "Listening on an https:// address needs a certificate. Enable TLS in the configuration, " +
                "or listen on http:// only when a reverse proxy is terminating TLS for you.");
    }
}
