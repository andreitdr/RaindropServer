using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using RaindropServer.Channels.Voice;
using RaindropServer.Voice.LiveKit.Protocol;

namespace RaindropServer.Voice.LiveKit;

/// <summary>
/// The slice of LiveKit's room API Raindrop uses: Twirp in its binary form, one POST of a protobuf
/// message per call to <c>/twirp/livekit.RoomService/{Method}</c> with an admin token. The messages are
/// the few vendored in <c>Protos/livekit_room_service.proto</c>.
/// </summary>
/// <remarks>
/// The one thing that is not protobuf is a refusal: Twirp always writes its errors as a small JSON object
/// (<c>{"code", "msg"}</c>), whatever the request was encoded in, so that is how they are read.
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
        var response = await CallAsync("ListRooms", null, new ListRoomsRequest(), ListRoomsResponse.Parser, cancellationToken);

        return response.Rooms.Select(room => room.Name).Where(name => name.Length > 0).ToList();
    }

    /// <summary>
    /// Who is in a room, and what each may publish. Nobody, when the room does not exist.
    /// </summary>
    /// <remarks>
    /// The sources are read back, not just <c>can_publish</c>, because the reconciler compares what
    /// LiveKit allows a participant against what Raindrop's record says it should — and the camera
    /// is the half of that which <c>can_publish</c> alone cannot express.
    /// </remarks>
    public async Task<IReadOnlyList<LiveKitParticipant>> ListParticipantsAsync(string room, CancellationToken cancellationToken)
    {
        var response = await CallAsync("ListParticipants", room, new ListParticipantsRequest { Room = room },
            ListParticipantsResponse.Parser, cancellationToken);

        return response.Participants
            .Where(participant => participant.State != ParticipantInfo.Types.State.Disconnected)
            .Select(participant => new LiveKitParticipant(
                participant.Identity,
                participant.State.ToString().ToUpperInvariant(),
                participant.Permission?.CanPublish ?? false,
                participant.Permission?.CanPublishSources.Contains(TrackSource.Camera) ?? false))
            .ToList();
    }

    /// <summary>
    /// Replaces a participant's permissions. LiveKit swaps the whole permission object rather than
    /// merging, so everything a Raindrop participant is allowed is sent every time — sending only
    /// <c>can_publish</c> would also switch off their ability to hear the call.
    /// </summary>
    public Task UpdatePermissionAsync(
        string room,
        string identity,
        bool canSpeak,
        bool canPublishCamera,
        CancellationToken cancellationToken) =>
        CallAsync("UpdateParticipant", room, new UpdateParticipantRequest
        {
            Room = room,
            Identity = identity,
            Permission = new ParticipantPermission
            {
                CanSubscribe = true,
                CanPublish = canSpeak || canPublishCamera,
                CanPublishData = false,
                // Built by the same never-empty rule as the join token's, since LiveKit reads an
                // empty list here as "every source" too.
                CanPublishSources =
                {
                    LiveKitJwt.PublishableSources(canSpeak, canPublishCamera).Select(SourceOf)
                },
                CanUpdateMetadata = false
            }
        }, ParticipantInfo.Parser, cancellationToken);

    private static TrackSource SourceOf(string name) => name switch
    {
        "camera" => TrackSource.Camera,
        "microphone" => TrackSource.Microphone,
        _ => TrackSource.Unknown
    };

    /// <summary>Removes a participant. Somebody already gone is not an error.</summary>
    public Task RemoveParticipantAsync(string room, string identity, CancellationToken cancellationToken) =>
        CallAsync("RemoveParticipant", room, new RoomParticipantIdentity { Room = room, Identity = identity },
            Ignored.Parser, cancellationToken);

    /// <summary>Closes a room, disconnecting everybody in it. A room already gone is not an error.</summary>
    public Task DeleteRoomAsync(string room, CancellationToken cancellationToken) =>
        CallAsync("DeleteRoom", room, new DeleteRoomRequest { Room = room }, Ignored.Parser, cancellationToken);

    /// <summary>
    /// Asks LiveKit for its rooms purely to find out whether it is there and whether it accepts this
    /// server's credentials — what <see cref="LiveKitStartupCheck"/> runs before the server listens.
    /// </summary>
    /// <remarks>
    /// Deliberately not routed through the ordinary call path, which exists to turn every failure
    /// into "try again later". A check wants the opposite: to tell an unreachable LiveKit from one
    /// that answered and refused this server, because the first is usually a sidecar still starting
    /// and the second is always a mistake in the configuration.
    /// </remarks>
    public async Task<LiveKitProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "twirp/livekit.RoomService/ListRooms")
        {
            Content = Protobuf(new ListRoomsRequest())
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwt.CreateAdminToken(null));

        HttpResponseMessage response;
        string text;
        var sentAt = DateTimeOffset.UtcNow;

        try
        {
            response = await _http.SendAsync(request, cancellationToken);
            text = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            return new LiveKitProbe(LiveKitReachability.Unreachable, Innermost(ex).Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new LiveKitProbe(LiveKitReachability.TimedOut, $"it did not answer within {_http.Timeout.TotalSeconds:0.##}s");
        }

        using (response)
        {
            // LiveKit stamps every response with its own clock, and every token this server mints is
            // timestamped too — so the difference between the two is worth knowing before a member
            // finds out by having a freshly minted token refused as expired.
            var skew = response.Headers.Date is { } theirs
                ? theirs - (sentAt + (DateTimeOffset.UtcNow - sentAt) / 2)
                : (TimeSpan?)null;

            if (response.IsSuccessStatusCode)
                return new LiveKitProbe(LiveKitReachability.Reachable, "it answered", skew);

            var error = TryParse(text);
            var message = Text(error, "msg") ?? text.Trim();

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new LiveKitProbe(LiveKitReachability.CredentialsRefused, Shorten(message), skew);

            // Twirp answers every one of its own methods, whatever it thinks of the request. A 404
            // means something else is listening on that port.
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed && error is null)
                return new LiveKitProbe(LiveKitReachability.NotLiveKit, $"{(int)response.StatusCode} {response.ReasonPhrase}", skew);

            return new LiveKitProbe(LiveKitReachability.Failed, $"{(int)response.StatusCode} {Shorten(message)}".TrimEnd(), skew);
        }
    }

    /// <summary>The exception at the bottom of the chain, which is the one that says what went wrong.</summary>
    private static Exception Innermost(Exception exception) =>
        exception.InnerException is { } inner ? Innermost(inner) : exception;

    /// <summary>Keeps a server's error text to one line, in case it answered with a page.</summary>
    private static string Shorten(string message) =>
        message.Length <= 200 ? message.ReplaceLineEndings(" ") : message[..200].ReplaceLineEndings(" ") + "…";

    public void Dispose() => _http.Dispose();

    // ── Plumbing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// What LiveKit says when a call about a room or one of its participants reached no node.
    /// </summary>
    /// <remarks>
    /// LiveKit answers these from the node holding the room rather than from its records, so it waits
    /// for that node and reports <c>unavailable</c> when none answers — which means the room is in its
    /// records but live nowhere. The usual way to see it is removing somebody at the moment they were
    /// leaving anyway: the state asked for, arrived at without us. Treating it as an error would have
    /// the reconciler report it every pass and keep asking for something already done.
    /// </remarks>
    private const string NoResponse = "no response from servers";

    /// <summary>
    /// Makes one call. A Twirp <c>not_found</c> — and, for a call about one room, an
    /// <c>unavailable</c> carrying <see cref="NoResponse"/> — answers with an empty response, since
    /// every caller here treats "already gone" as success.
    /// </summary>
    private async Task<TResponse> CallAsync<TResponse>(
        string method, string? room, IMessage body, MessageParser<TResponse> parser, CancellationToken cancellationToken)
        where TResponse : IMessage<TResponse>, new()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"twirp/livekit.RoomService/{method}")
        {
            Content = Protobuf(body)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwt.CreateAdminToken(room));

        HttpResponseMessage response;
        byte[] bytes;
        try
        {
            response = await _http.SendAsync(request, cancellationToken);
            bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
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
            {
                try
                {
                    return parser.ParseFrom(bytes);
                }
                catch (InvalidProtocolBufferException ex)
                {
                    throw new VoiceTransportException($"LiveKit answered {method} with something that is not its protobuf.", ex);
                }
            }

            var text = Encoding.UTF8.GetString(bytes);
            var error = TryParse(text);
            var code = Text(error, "code");
            var message = Text(error, "msg") ?? text;

            if (response.StatusCode == HttpStatusCode.NotFound || code == "not_found")
                return new TResponse();

            if (room is not null && code == "unavailable" && message.Contains(NoResponse, StringComparison.Ordinal))
                return new TResponse();

            throw new VoiceTransportException(
                $"LiveKit refused {method}: {(int)response.StatusCode} {code ?? ""} {message}".TrimEnd());
        }
    }

    /// <summary>A request body in Twirp's binary encoding.</summary>
    private static ByteArrayContent Protobuf(IMessage body)
    {
        var content = new ByteArrayContent(body.ToByteArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/protobuf");
        return content;
    }

    /// <summary>A Twirp error body: always JSON, by the Twirp specification, whatever the request was.</summary>
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

    private static string? Text(JsonNode? node, string field) =>
        node?[FieldName(node, field)] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

/// <param name="Identity">Their identity in the room — a Raindrop member's UUID, for anybody Raindrop admitted.</param>
/// <param name="State"><c>JOINING</c>, <c>JOINED</c>, <c>ACTIVE</c> or <c>DISCONNECTED</c>.</param>
/// <param name="CanPublish">Whether LiveKit currently accepts their microphone.</param>
public sealed record LiveKitParticipant(
    string Identity,
    string State,
    bool CanPublish,
    bool CanPublishCamera);

/// <summary>What asking LiveKit whether it is there came back with.</summary>
public enum LiveKitReachability
{
    /// <summary>LiveKit answered, and accepted this server's credentials.</summary>
    Reachable,

    /// <summary>Nothing accepted a connection at that address.</summary>
    Unreachable,

    /// <summary>Something accepted the connection but did not answer in time.</summary>
    TimedOut,

    /// <summary>LiveKit answered, and refused the API key or secret it was given.</summary>
    CredentialsRefused,

    /// <summary>Something is listening there, but it is not LiveKit's API.</summary>
    NotLiveKit,

    /// <summary>LiveKit answered with something else that stops it being usable.</summary>
    Failed
}

/// <param name="Result">How the attempt went.</param>
/// <param name="Detail">What LiveKit or the network said, for the owner to read.</param>
/// <param name="ClockSkew">
/// How far LiveKit's clock is ahead of this server's, when it answered at all. Tokens carry the time
/// they were minted and the time they expire, so a large difference makes them unusable.
/// </param>
public sealed record LiveKitProbe(LiveKitReachability Result, string Detail, TimeSpan? ClockSkew = null);
