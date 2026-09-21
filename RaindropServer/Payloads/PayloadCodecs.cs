using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace RaindropServer.Payloads;

/// <summary>
/// Picks the wire format for a request and its response. A client asks for the binary format with
/// <c>Content-Type: application/x-msgpack</c> and <c>Accept: application/x-msgpack</c>; anything
/// that says nothing gets JSON, so curl still works.
/// </summary>
public sealed class PayloadCodecs
{
    private readonly IReadOnlyList<IPayloadCodec> _codecs;

    public PayloadCodecs(IEnumerable<IPayloadCodec> codecs)
    {
        _codecs = codecs.ToList();

        if (_codecs.Count == 0)
            throw new ArgumentException("At least one payload codec must be registered.", nameof(codecs));

        Default = _codecs.FirstOrDefault(c => c.ContentType == "application/json") ?? _codecs[0];
    }

    /// <summary>The format used when the client expresses no preference.</summary>
    public IPayloadCodec Default { get; }

    /// <summary>Every registered media type, for the client to discover at handshake time.</summary>
    public IReadOnlyList<string> ContentTypes => _codecs.Select(c => c.ContentType).ToList();

    /// <summary>
    /// The codec for reading this request's body, chosen by <c>Content-Type</c>.
    /// </summary>
    /// <exception cref="ApiException">The request carries a body in a format this server does not read.</exception>
    public IPayloadCodec ForRequest(HttpRequest request)
    {
        var contentType = request.ContentType;

        if (string.IsNullOrWhiteSpace(contentType))
            throw ApiException.UnsupportedMediaType("missing_content_type",
                $"A request body needs a Content-Type. This server reads: {string.Join(", ", ContentTypes)}.");

        if (!MediaTypeHeaderValue.TryParse(contentType, out var parsed))
            throw ApiException.UnsupportedMediaType("unsupported_media_type", $"'{contentType}' is not a valid media type.");

        var mediaType = parsed.MediaType.Value ?? string.Empty;

        return _codecs.FirstOrDefault(c => c.Matches(mediaType))
               ?? throw ApiException.UnsupportedMediaType("unsupported_media_type",
                   $"This server does not read '{mediaType}'. It reads: {string.Join(", ", ContentTypes)}.");
    }

    /// <summary>
    /// The codec for writing the response, chosen by <c>Accept</c>. A client that accepts anything
    /// gets the format it sent, so a request never has to state its preference twice.
    /// </summary>
    public IPayloadCodec ForResponse(HttpRequest request)
    {
        foreach (var value in request.Headers.Accept)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            foreach (var candidate in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!MediaTypeHeaderValue.TryParse(candidate, out var parsed))
                    continue;

                var mediaType = parsed.MediaType.Value ?? string.Empty;
                var match = _codecs.FirstOrDefault(c => c.Matches(mediaType));

                if (match is not null)
                    return match;
            }
        }

        // Nothing specific was asked for: mirror the request body's format when there was one.
        return TryForRequestBody(request) ?? Default;
    }

    private IPayloadCodec? TryForRequestBody(HttpRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ContentType)
            || !MediaTypeHeaderValue.TryParse(request.ContentType, out var parsed))
            return null;

        var mediaType = parsed.MediaType.Value ?? string.Empty;
        return _codecs.FirstOrDefault(c => c.Matches(mediaType));
    }
}

/// <summary>Reaching the negotiated codec from anywhere that has an <see cref="HttpContext"/>.</summary>
public static class PayloadExtensions
{
    /// <summary>
    /// Writes a value in whichever format the caller asked for. Available to raw
    /// <see cref="IEndpoint"/> implementations, which have no adapter doing it for them.
    /// </summary>
    public static ValueTask WritePayloadAsync<T>(this HttpContext context, T value, CancellationToken cancellationToken)
    {
        // Falling back to JSON keeps error responses working even for a request that never got as
        // far as negotiating a format.
        var codecs = context.RequestServices.GetService<PayloadCodecs>();

        return codecs is null
            ? new ValueTask(context.Response.WriteAsJsonAsync(value, cancellationToken))
            : codecs.ForResponse(context.Request).WriteAsync(context.Response, value, cancellationToken);
    }

    /// <summary>
    /// Reads and validates a request body, failing with a 400 rather than an empty object when the
    /// body is missing.
    /// </summary>
    public static async ValueTask<T> ReadPayloadAsync<T>(
        this HttpContext context, IPayloadCodec codec, CancellationToken cancellationToken)
    {
        var value = await codec.ReadAsync<T>(context.Request, cancellationToken);

        if (value is null)
            throw ApiException.BadRequest("missing_body", "A request body is required.");

        return value;
    }
}
