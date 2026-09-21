using Microsoft.AspNetCore.Http;

namespace RaindropServer.Payloads;

/// <summary>
/// One wire format for request and response bodies. Endpoints never see this: they work in terms of
/// their own request and response types, and the format is negotiated per request from the
/// <c>Content-Type</c> and <c>Accept</c> headers.
/// </summary>
public interface IPayloadCodec
{
    /// <summary>The media type this codec writes, e.g. <c>application/x-msgpack</c>.</summary>
    string ContentType { get; }

    /// <summary>Whether this codec handles the given media type (the parameters are already stripped).</summary>
    bool Matches(string mediaType);

    /// <summary>Reads the request body. Returns null when the body is empty.</summary>
    ValueTask<T?> ReadAsync<T>(HttpRequest request, CancellationToken cancellationToken);

    /// <summary>Writes a value as the response body, setting the content type.</summary>
    ValueTask WriteAsync<T>(HttpResponse response, T value, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the encoding is binary. The event stream carries a binary format in binary WebSocket
    /// frames and a text one in text frames, so a client can tell them apart without asking.
    /// </summary>
    bool IsBinary { get; }

    /// <summary>
    /// Encodes a value on its own, for a transport with no <see cref="HttpResponse"/> to write to —
    /// the event stream, where one encoding is sent to every connection that asked for this format.
    /// </summary>
    byte[] Encode<T>(T value);
}
