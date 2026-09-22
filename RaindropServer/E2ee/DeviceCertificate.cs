using System.Buffers.Binary;
using System.Text;
using RaindropServer.Security;

namespace RaindropServer.E2ee;

/// <summary>
/// A user identity key's signed statement that a device signature key belongs to one of the user's
/// devices. Clients make these (<c>RaindropCrypto/crates/raindrop_crypto/src/identity.rs</c>); the
/// server reads them only to decide what to store and who may bind a session. Every client checks
/// them again for itself, since the server is not trusted with that decision.
/// </summary>
/// <remarks>
/// Wire form, big-endian, each <c>field</c> a u16 length followed by that many bytes:
/// <c>version: u8 = 1 | user_id | device_id | device_signature_key | user_identity_key |
/// created_at_unix: i64 | signature</c>. The signature covers
/// <c>"raindrop/device-certificate/v1\n" | version | every field above up to created_at</c>.
/// </remarks>
public sealed record DeviceCertificate(
    Guid UserId,
    Guid DeviceId,
    byte[] DeviceSignatureKey,
    byte[] UserIdentityKey,
    long CreatedAtUnix,
    byte[] Signature)
{
    // The ids exactly as the client wrote them. The signature covers these bytes, so they are what
    // gets checked; re-formatting a parsed Guid could differ in case and break verification.
    private string _userIdText = "";
    private string _deviceIdText = "";

    private const byte Version = 1;
    private static readonly byte[] Context = "raindrop/device-certificate/v1\n"u8.ToArray();

    /// <summary>The longest certificate accepted; a real one is about 200 bytes.</summary>
    public const int MaximumLength = 1024;

    /// <summary>Parses a certificate. Throws <see cref="ApiException"/> (400) when it is malformed.</summary>
    public static DeviceCertificate Parse(byte[]? bytes)
    {
        if (bytes is null || bytes.Length is 0 or > MaximumLength)
            throw Malformed("A device certificate is required.");

        var reader = new Reader(bytes);
        if (reader.Take(1)[0] != Version)
            throw Malformed("Unknown device certificate version.");

        var (userId, userIdText) = reader.Guid();
        var (deviceId, deviceIdText) = reader.Guid();
        var deviceKey = reader.Field().ToArray();
        var identityKey = reader.Field().ToArray();
        var createdAt = BinaryPrimitives.ReadInt64BigEndian(reader.Take(8));
        var signature = reader.Field().ToArray();

        if (!reader.AtEnd)
            throw Malformed("Trailing bytes after the device certificate.");

        if (deviceKey.Length != Ed25519Signatures.PublicKeyLength || identityKey.Length != Ed25519Signatures.PublicKeyLength)
            throw Malformed("Device certificate keys must be Ed25519 public keys.");

        return new DeviceCertificate(userId, deviceId, deviceKey, identityKey, createdAt, signature)
        {
            _userIdText = userIdText,
            _deviceIdText = deviceIdText
        };
    }

    /// <summary>Whether the signature is valid for the identity key the certificate carries.</summary>
    public bool IsSignatureValid() =>
        Ed25519Signatures.Verify(UserIdentityKey, SignedBytes(), Signature);

    private byte[] SignedBytes()
    {
        var output = new List<byte>(Context) { Version };
        WriteField(output, Encoding.UTF8.GetBytes(_userIdText));
        WriteField(output, Encoding.UTF8.GetBytes(_deviceIdText));
        WriteField(output, DeviceSignatureKey);
        WriteField(output, UserIdentityKey);
        Span<byte> created = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(created, CreatedAtUnix);
        output.AddRange(created.ToArray());
        return output.ToArray();
    }

    private static void WriteField(List<byte> output, byte[] field)
    {
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)field.Length);
        output.AddRange(length.ToArray());
        output.AddRange(field);
    }

    private static ApiException Malformed(string message) => ApiException.BadRequest("invalid_certificate", message);

    private ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private ReadOnlySpan<byte> _rest = bytes;

        public readonly bool AtEnd => _rest.IsEmpty;

        public ReadOnlySpan<byte> Take(int count)
        {
            if (_rest.Length < count)
                throw Malformed("Truncated device certificate.");

            var head = _rest[..count];
            _rest = _rest[count..];
            return head;
        }

        public ReadOnlySpan<byte> Field() => Take(BinaryPrimitives.ReadUInt16BigEndian(Take(2)));

        // Ids travel as the text of a UUID, which is what the client signs; they must parse as one
        // here, since every other table knows users and devices by UUID.
        public (System.Guid Id, string Text) Guid()
        {
            var text = Encoding.UTF8.GetString(Field());
            return System.Guid.TryParse(text, out var id) && id != System.Guid.Empty
                ? (id, text)
                : throw Malformed("Device certificate ids must be UUIDs.");
        }
    }
}
