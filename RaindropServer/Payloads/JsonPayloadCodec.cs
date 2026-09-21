using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace RaindropServer.Payloads;

/// <summary>
/// The JSON wire format — the default, and the one to reach for when debugging by hand, since a
/// request can be made with nothing more than curl. Property names are camelCased.
/// </summary>
public sealed class JsonPayloadCodec : IPayloadCodec
{
    public string ContentType => "application/json";

    public bool Matches(string mediaType) =>
        mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("text/json", StringComparison.OrdinalIgnoreCase)
        || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);

    public async ValueTask<T?> ReadAsync<T>(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await request.ReadFromJsonAsync<T>(cancellationToken);
        }
        catch (JsonException ex)
        {
            throw ApiException.BadRequest("invalid_payload", $"The request body is not valid JSON: {ex.Message}");
        }
    }

    public async ValueTask WriteAsync<T>(HttpResponse response, T value, CancellationToken cancellationToken) =>
        await response.WriteAsJsonAsync(value, cancellationToken);

    public bool IsBinary => false;

    // The web defaults are what WriteAsJsonAsync uses, so an event is spelled exactly like the same
    // object would be in a response body.
    public byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonSerializerOptions.Web);
}
