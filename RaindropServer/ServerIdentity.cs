namespace RaindropServer;

/// <summary>
/// What a client can learn about this server before it has an account here — enough to decide
/// whether it trusts the connection and which wire format to use.
/// </summary>
/// <param name="ProtocolVersion">The Raindrop protocol this server speaks.</param>
/// <param name="TlsFingerprint">
/// The SHA-256 fingerprint of the certificate this server presents, or null when TLS is terminated
/// somewhere else. A client pins this the first time it connects and refuses to continue if it
/// changes later — unless <paramref name="TlsBindingSignature"/> shows the pinned
/// <paramref name="IdentityKey"/> vouches for the new one.
/// </param>
/// <param name="IdentityKey">
/// The server's long-lived public key (an uncompressed P-256 point), which a client pins alongside
/// the fingerprint. See <see cref="Security.ServerIdentityKey"/>.
/// </param>
/// <param name="TlsBindingSignature">
/// <paramref name="IdentityKey"/>'s signature over <paramref name="TlsFingerprint"/>, or null when
/// either is missing.
/// </param>
public sealed record ServerIdentity(
    string ProtocolVersion,
    string? TlsFingerprint,
    byte[]? IdentityKey = null,
    byte[]? TlsBindingSignature = null)
{
    /// <summary>The protocol version this build implements.</summary>
    public const string CurrentProtocolVersion = "1";

    /// <summary>
    /// Optional features this server offers, as open strings a plugin may add to — a client checks
    /// for one before it relies on the feature, instead of reading it off the protocol version.
    /// </summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>Advertised when the server signs its certificate with <see cref="IdentityKey"/>.</summary>
    public const string TlsIdentityBindingCapability = "tls.identity-binding.v1";

    /// <summary>
    /// Advertised when the server keeps end-to-end encryption identities: devices, identity keys,
    /// session binding, key backups and device linking (<c>/api/e2ee/*</c>).
    /// </summary>
    public const string E2eeDevicesCapability = "e2ee.devices.v1";

    /// <summary>
    /// Advertised when the server runs the MLS delivery service for channel groups
    /// (<c>/api/e2ee/groups/*</c>, key packages, Welcomes).
    /// </summary>
    public const string E2eeDeliveryCapability = "e2ee.delivery.v1";

    /// <summary>
    /// Advertised when calls on this server are end-to-end encrypted: clients encrypt their media with
    /// a key from the voice channel's MLS group, so the media server forwards audio it cannot hear.
    /// A client refuses to join a call it cannot encrypt on a server that says this.
    /// </summary>
    public const string E2eeVoiceCapability = "e2ee.voice.v1";

    /// <summary>
    /// Advertised when members may add end-to-end encrypted files to messages
    /// (<c>/api/attachments/*</c>, and <c>attachmentIds</c> on <c>send-encrypted</c>). Left out when the
    /// owner has switched attachments off.
    /// </summary>
    public const string AttachmentsCapability = "attachments.v1";
}
