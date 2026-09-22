using RaindropServer;
using RaindropServer.Payloads;

namespace ServerCli.Endpoints;

/// <summary>
/// GET /api/server/info — the handshake. A client calls this before it has an account, to check the
/// protocol version, compare the TLS fingerprint against the one it pinned for this server, and
/// discover which wire formats are on offer.
/// </summary>
public sealed class ServerInfoEndpoint : IEndpoint
{
    private readonly ServerIdentity _identity;
    private readonly PayloadCodecs _codecs;

    public ServerInfoEndpoint(ServerIdentity identity, PayloadCodecs codecs)
    {
        _identity = identity;
        _codecs = codecs;
    }

    public string Route => "/api/server/info";
    public HttpMethod Method => HttpMethod.Get;

    public Task HandleAsync(HttpContext context, CancellationToken cancellationToken) =>
        EndpointResponse.WriteAsync(context, cancellationToken, _ => Task.FromResult(
            new ServerInfoResponse(
                _identity.ProtocolVersion,
                _identity.TlsFingerprint,
                _codecs.ContentTypes,
                _identity.IdentityKey,
                _identity.TlsBindingSignature,
                _identity.Capabilities)));
}

/// <param name="ProtocolVersion">The Raindrop protocol this server speaks.</param>
/// <param name="TlsFingerprint">The SHA-256 fingerprint of this server's certificate, for pinning.</param>
/// <param name="PayloadFormats">
/// The media types this server reads and writes, in preference order. Send one as both
/// <c>Content-Type</c> and <c>Accept</c>.
/// </param>
/// <param name="IdentityKey">
/// The server's long-lived P-256 public key (uncompressed point). Pin it with the fingerprint.
/// </param>
/// <param name="TlsBindingSignature">
/// <paramref name="IdentityKey"/>'s ECDSA-SHA256 signature (<c>r‖s</c>) over
/// <c>"raindrop/tls-binding/v1\n" + fingerprint</c>, the fingerprint in lower-case hex without
/// separators. A client whose pinned fingerprint no longer matches accepts the new certificate only
/// when the pinned identity key signed the one it was actually presented.
/// </param>
/// <param name="Capabilities">Optional features this server offers.</param>
public record ServerInfoResponse(
    string ProtocolVersion,
    string? TlsFingerprint,
    IReadOnlyList<string> PayloadFormats,
    byte[]? IdentityKey,
    byte[]? TlsBindingSignature,
    IReadOnlyList<string> Capabilities);
