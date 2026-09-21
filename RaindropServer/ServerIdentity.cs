namespace RaindropServer;

/// <summary>
/// What a client can learn about this server before it has an account here — enough to decide
/// whether it trusts the connection and which wire format to use.
/// </summary>
/// <param name="ProtocolVersion">The Raindrop protocol this server speaks.</param>
/// <param name="TlsFingerprint">
/// The SHA-256 fingerprint of the certificate this server presents, or null when TLS is terminated
/// somewhere else. A client pins this the first time it connects and refuses to continue if it
/// changes later.
/// </param>
public sealed record ServerIdentity(string ProtocolVersion, string? TlsFingerprint)
{
    /// <summary>The protocol version this build implements.</summary>
    public const string CurrentProtocolVersion = "1";
}
