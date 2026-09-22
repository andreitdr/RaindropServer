using System.Buffers.Binary;

namespace RaindropServer.E2ee;

/// <summary>The MLS wire formats (RFC 9420 §6).</summary>
public enum MlsWireFormat : ushort
{
    PublicMessage = 1,
    PrivateMessage = 2,
    Welcome = 3,
    GroupInfo = 4,
    KeyPackage = 5
}

/// <summary>What a group message carries (RFC 9420 §6).</summary>
public enum MlsContentType : byte
{
    Application = 1,
    Proposal = 2,
    Commit = 3
}

/// <summary>Who sent a public group message (RFC 9420 §6).</summary>
public enum MlsSenderType : byte
{
    Member = 1,
    External = 2,
    NewMemberProposal = 3,
    NewMemberCommit = 4
}

/// <summary>
/// The cleartext envelope of an MLS message: the few fields a delivery service is allowed to see,
/// which is all the server needs to order and route traffic it cannot read.
/// </summary>
/// <param name="WireFormat">What kind of message it is.</param>
/// <param name="GroupId">The group, for group messages and group infos.</param>
/// <param name="Epoch">The epoch it was sent in (group messages), or the group's epoch (group infos).</param>
/// <param name="ContentType">For group messages: application data, proposal or commit.</param>
/// <param name="SenderType">For public messages only; a private message's sender is encrypted.</param>
public sealed record MlsEnvelope(
    MlsWireFormat WireFormat,
    byte[]? GroupId,
    ulong Epoch,
    MlsContentType? ContentType,
    MlsSenderType? SenderType);

/// <summary>
/// Reads the cleartext headers of MLS messages (RFC 9420's TLS presentation language, with its
/// variable-length vector sizes). It reads only headers and never trusts what follows them: whether
/// the rest is well formed is for the clients to decide.
/// </summary>
public static class MlsFraming
{
    private const ushort Mls10 = 1;

    /// <summary>
    /// Parses the envelope of <paramref name="message"/>. Throws <see cref="ApiException"/> (400,
    /// <c>invalid_mls_message</c>) when the header is malformed.
    /// </summary>
    public static MlsEnvelope Parse(ReadOnlySpan<byte> message)
    {
        var reader = new Reader(message);
        if (reader.UInt16() != Mls10)
            throw Invalid("Not an MLS 1.0 message.");

        var wireFormat = (MlsWireFormat)reader.UInt16();
        switch (wireFormat)
        {
            case MlsWireFormat.PublicMessage:
            {
                var groupId = reader.Vector().ToArray();
                var epoch = reader.UInt64();
                var senderType = (MlsSenderType)reader.UInt8();
                if (senderType is MlsSenderType.Member or MlsSenderType.External)
                    reader.UInt32();
                else if (senderType is not (MlsSenderType.NewMemberProposal or MlsSenderType.NewMemberCommit))
                    throw Invalid("Unknown sender type.");
                reader.Vector(); // authenticated_data
                return new MlsEnvelope(wireFormat, groupId, epoch, ContentType(reader.UInt8()), senderType);
            }

            case MlsWireFormat.PrivateMessage:
            {
                var groupId = reader.Vector().ToArray();
                var epoch = reader.UInt64();
                return new MlsEnvelope(wireFormat, groupId, epoch, ContentType(reader.UInt8()), null);
            }

            case MlsWireFormat.GroupInfo:
            {
                reader.UInt16(); // group_context.version
                reader.UInt16(); // group_context.cipher_suite
                var groupId = reader.Vector().ToArray();
                return new MlsEnvelope(wireFormat, groupId, reader.UInt64(), null, null);
            }

            case MlsWireFormat.Welcome:
            case MlsWireFormat.KeyPackage:
                return new MlsEnvelope(wireFormat, null, 0, null, null);

            default:
                throw Invalid("Unknown MLS wire format.");
        }
    }

    /// <summary>
    /// The MLS group id for a channel: its UUID as 16 bytes in RFC 4122 (big-endian) order, the same
    /// bytes the client derives from the channel id, so both sides name the same group without a
    /// lookup.
    /// </summary>
    public static byte[] GroupIdFor(Guid channelId) => channelId.ToByteArray(bigEndian: true);

    private static MlsContentType ContentType(byte value) =>
        value is >= 1 and <= 3 ? (MlsContentType)value : throw Invalid("Unknown content type.");

    private static ApiException Invalid(string message) => ApiException.BadRequest("invalid_mls_message", message);

    private ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private ReadOnlySpan<byte> _rest = bytes;

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || _rest.Length < count)
                throw Invalid("Truncated MLS message.");
            var head = _rest[..count];
            _rest = _rest[count..];
            return head;
        }

        public byte UInt8() => Take(1)[0];
        public ushort UInt16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
        public uint UInt32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));
        public ulong UInt64() => BinaryPrimitives.ReadUInt64BigEndian(Take(8));

        /// <summary>A vector with an RFC 9420 §2.1.2 variable-length size: 1, 2 or 4 bytes, by the top two bits.</summary>
        public ReadOnlySpan<byte> Vector()
        {
            var first = UInt8();
            var length = (first >> 6) switch
            {
                0 => first & 0x3F,
                1 => ((first & 0x3F) << 8) | UInt8(),
                2 => (int)((((uint)first & 0x3F) << 24) | ((uint)UInt8() << 16) | ((uint)UInt8() << 8) | UInt8()),
                _ => throw Invalid("Invalid vector length.")
            };
            return Take(length);
        }
    }
}
