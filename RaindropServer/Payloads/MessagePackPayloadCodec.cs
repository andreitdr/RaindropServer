using MessagePack;
using MessagePack.Resolvers;
using Microsoft.AspNetCore.Http;

namespace RaindropServer.Payloads;

/// <summary>
/// The binary wire format, and what a real client should use: MessagePack encodes the same objects
/// in a fraction of the bytes JSON needs and skips text parsing entirely.
/// </summary>
/// <remarks>
/// Serialization is contractless, so the contracts stay plain records with no attributes — the
/// trade-off is that keys are the property names verbatim (<c>UserId</c>), where the JSON codec
/// camelCases them (<c>userId</c>). A .NET client sharing these contract types never notices;
/// a client in another language needs to know.
/// </remarks>
public sealed class MessagePackPayloadCodec : IPayloadCodec
{
    /// <summary>
    /// Contractless so the contracts need no attributes, and explicitly marked untrusted: that turns
    /// on the guards against hostile payloads, which matters for a public, unauthenticated endpoint.
    /// Typeless resolvers are deliberately not used — they would let a caller name the type to build.
    /// </summary>
    private static readonly MessagePackSerializerOptions Options =
        ContractlessStandardResolver.Options.WithSecurity(MessagePackSecurity.UntrustedData);

    public string ContentType => "application/x-msgpack";

    public bool Matches(string mediaType) =>
        mediaType.Equals("application/x-msgpack", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("application/msgpack", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("application/vnd.msgpack", StringComparison.OrdinalIgnoreCase);

    public async ValueTask<T?> ReadAsync<T>(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await MessagePackSerializer.DeserializeAsync<T>(request.Body, Options, cancellationToken);
        }
        catch (MessagePackSerializationException ex)
        {
            throw ApiException.BadRequest("invalid_payload", $"The request body is not valid MessagePack: {ex.Message}");
        }
    }

    public async ValueTask WriteAsync<T>(HttpResponse response, T value, CancellationToken cancellationToken)
    {
        response.ContentType = ContentType;
        await MessagePackSerializer.SerializeAsync(response.Body, value, Options, cancellationToken);
    }

    public bool IsBinary => true;

    public byte[] Encode<T>(T value) => MessagePackSerializer.Serialize(value, Options);
}
