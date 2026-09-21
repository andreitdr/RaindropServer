using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RaindropServer.Channels.Voice;

namespace RaindropServer.Voice.LiveKit;

/// <summary>
/// The slice of LiveKit's room API Raindrop uses: Twirp over JSON, one POST per call to
/// <c>/twirp/livekit.RoomService/{Method}</c> with an admin token.
/// </summary>
/// <remarks>
/// Responses are read loosely on purpose. Twirp's JSON is protojson, which may name fields in
/// <c>snake_case</c> or <c>camelCase</c> depending on the server's settings, may leave out fields that
/// hold their default, and writes 64-bit numbers as strings; every read here copes with all three.
/// Anything that goes wrong reaching LiveKit becomes a <see cref="VoiceTransportException"/>, which the
/// voice subsystem retries on its next pass rather than failing a request over.
/// </remarks>
public sealed class LiveKitRoomClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly LiveKitJwt _jwt;

    public LiveKitRoomClient(LiveKitOptions options, LiveKitJwt jwt)
    {
        _jwt = jwt;
        _http = new HttpClient { BaseAddress = options.ApiBase, Timeout = options.RequestTimeout };
    }

    /// <summary>The names of every room LiveKit currently has open.</summary>
    public async Task<IReadOnlyList<string>> ListRoomsAsync(CancellationToken cancellationToken)
    {
        var response = await CallAsync("ListRooms", null, new JsonObject(), cancellationToken);

        return Array(response, "rooms")
            .Select(room => Text(room, "name"))
            .OfType<string>()
            .ToList();
    }

    /// <summary>Who is in a room, and whether each may publish. Nobody, when the room does not exist.</summary>
    public async Task<IReadOnlyList<LiveKitParticipant>> ListParticipantsAsync(string room, CancellationToken cancellationToken)
    {
        var response = await CallAsync("ListParticipants", room, new JsonObject { ["room"] = room }, cancellationToken);

        return Array(response, "participants")
            .Select(participant => new LiveKitParticipant(
                Text(participant, "identity") ?? "",
                Text(participant, "state") ?? "ACTIVE",
                Flag(participant?[FieldName(participant, "permission")], "can_publish", "canPublish")))
            .Where(participant => participant.State != "DISCONNECTED")
            .ToList();
    }

    /// <summary>
    /// Replaces a participant's permissions. LiveKit swaps the whole permission object rather than
    /// merging, so everything a Raindrop participant is allowed is sent every time — sending only
    /// <c>can_publish</c> would also switch off their ability to hear the call.
    /// </summary>
    public Task UpdatePermissionAsync(string room, string identity, bool canPublish, CancellationToken cancellationToken) =>
        CallAsync("UpdateParticipant", room, new JsonObject
        {
            ["room"] = room,
            ["identity"] = identity,
            ["permission"] = new JsonObject
            {
                ["can_subscribe"] = true,
                ["can_publish"] = canPublish,
                ["can_publish_data"] = false,
                ["can_publish_sources"] = new JsonArray("MICROPHONE"),
                ["can_update_metadata"] = false
            }
        }, cancellationToken);

    /// <summary>Removes a participant. Somebody already gone is not an error.</summary>
    public Task RemoveParticipantAsync(string room, string identity, CancellationToken cancellationToken) =>
        CallAsync("RemoveParticipant", room, new JsonObject { ["room"] = room, ["identity"] = identity }, cancellationToken);

    /// <summary>Closes a room, disconnecting everybody in it. A room already gone is not an error.</summary>
    public Task DeleteRoomAsync(string room, CancellationToken cancellationToken) =>
        CallAsync("DeleteRoom", room, new JsonObject { ["room"] = room }, cancellationToken);

    public void Dispose() => _http.Dispose();

    // ── Plumbing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Makes one call. A Twirp <c>not_found</c> answers with an empty object, since every caller here
    /// treats "already gone" as success.
    /// </summary>
    private async Task<JsonNode?> CallAsync(string method, string? room, JsonObject body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"twirp/livekit.RoomService/{method}")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwt.CreateAdminToken(room));

        HttpResponseMessage response;
        string text;
        try
        {
            response = await _http.SendAsync(request, cancellationToken);
            text = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new VoiceTransportException($"LiveKit is unreachable at {_http.BaseAddress}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new VoiceTransportException($"LiveKit did not answer {method} in time.", ex);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
                return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text);

            var error = TryParse(text);
            var code = Text(error, "code");

            if (response.StatusCode == HttpStatusCode.NotFound || code == "not_found")
                return new JsonObject();

            throw new VoiceTransportException(
                $"LiveKit refused {method}: {(int)response.StatusCode} {code ?? ""} {Text(error, "msg") ?? text}".TrimEnd());
        }
    }

    private static JsonNode? TryParse(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whichever spelling of a field this response used — snake_case or camelCase.</summary>
    private static string FieldName(JsonNode? node, string snake)
    {
        if (node is JsonObject obj && !obj.ContainsKey(snake))
        {
            var parts = snake.Split('_');
            var camel = parts[0] + string.Concat(parts.Skip(1).Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
            if (obj.ContainsKey(camel))
                return camel;
        }

        return snake;
    }

    private static IEnumerable<JsonNode?> Array(JsonNode? node, string field) =>
        node?[FieldName(node, field)] as JsonArray ?? [];

    private static string? Text(JsonNode? node, string field) =>
        node?[FieldName(node, field)] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>A boolean that protojson leaves out entirely when it is false.</summary>
    private static bool Flag(JsonNode? node, string snake, string camel) =>
        (node?[snake] ?? node?[camel]) is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
}

/// <param name="Identity">Their identity in the room — a Raindrop member's UUID, for anybody Raindrop admitted.</param>
/// <param name="State"><c>JOINING</c>, <c>JOINED</c>, <c>ACTIVE</c> or <c>DISCONNECTED</c>.</param>
/// <param name="CanPublish">Whether LiveKit currently accepts their microphone.</param>
public sealed record LiveKitParticipant(string Identity, string State, bool CanPublish);
