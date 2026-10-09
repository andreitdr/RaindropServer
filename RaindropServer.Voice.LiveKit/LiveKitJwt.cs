using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RaindropServer.Channels.Voice;

namespace RaindropServer.Voice.LiveKit;

/// <summary>
/// LiveKit's access tokens: HS256 JWTs signed with the API secret, carrying a <c>video</c> grant that
/// says what the bearer may do. Minted for clients joining a call, for Raindrop's own calls to the room
/// API, and checked again by the signal relay.
/// </summary>
public sealed class LiveKitJwt
{
    /// <summary>Tolerated clock difference between Raindrop and LiveKit.</summary>
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(10);

    /// <summary>Raindrop's own API calls are made immediately; their tokens need not live long.</summary>
    private static readonly TimeSpan AdminLifetime = TimeSpan.FromMinutes(1);

    private readonly LiveKitOptions _options;
    private readonly TimeProvider _time;
    private readonly byte[] _secret;

    public LiveKitJwt(LiveKitOptions options, TimeProvider? time = null)
    {
        _options = options;
        _time = time ?? TimeProvider.System;
        _secret = Encoding.UTF8.GetBytes(options.ApiSecret);
    }

    /// <summary>The room a voice channel's call lives in.</summary>
    public string RoomFor(Guid channelId) => _options.RoomPrefix + channelId.ToString("D");

    /// <summary>The voice channel a room belongs to, or null for a room this server does not own.</summary>
    public Guid? ChannelFor(string? room) =>
        room is not null && room.StartsWith(_options.RoomPrefix, StringComparison.Ordinal) &&
        Guid.TryParse(room.AsSpan(_options.RoomPrefix.Length), out var channelId)
            ? channelId
            : null;

    /// <summary>
    /// A token that lets one member into one room. They may publish exactly the sources
    /// <see cref="VoiceGrant.Publish"/> allows and nothing else; they cannot send data or rewrite
    /// their own metadata, so nothing about them in the call is theirs to forge.
    /// </summary>
    public (string Token, DateTime ExpiresAtUtc) CreateParticipantToken(VoiceGrant grant)
    {
        var now = _time.GetUtcNow();
        var expires = now + _options.TokenLifetime;
        var identity = grant.UserId.ToString("D");
        var sources = PublishableSources(grant.Publish);

        var claims = new JsonObject
        {
            ["iss"] = _options.ApiKey,
            ["sub"] = identity,
            ["jti"] = identity,
            ["name"] = grant.DisplayName,
            ["nbf"] = (now - Skew).ToUnixTimeSeconds(),
            ["exp"] = expires.ToUnixTimeSeconds(),
            ["video"] = new JsonObject
            {
                ["room"] = RoomFor(grant.ChannelId),
                ["roomJoin"] = true,
                ["canSubscribe"] = true,
                ["canPublish"] = grant.Publish.Any,
                // No data channel at all. Calls are end-to-end encrypted with keys from the voice
                // channel's MLS group, which travel through Raindrop's delivery service, never
                // through LiveKit: nothing that could carry key material passes through it.
                ["canPublishData"] = false,
                ["canPublishSources"] = new JsonArray(sources.Select(name => (JsonNode?)name).ToArray()),
                ["canUpdateOwnMetadata"] = false
            }
        };

        return (Sign(claims), expires.UtcDateTime);
    }

    /// <summary>
    /// The sources a participant may publish, as LiveKit names them.
    /// </summary>
    /// <remarks>
    /// Never empty, which is the point of this living in one place: LiveKit reads an empty
    /// <c>canPublishSources</c> as "every source", so a participant allowed nothing would be allowed
    /// everything. Paired with <c>canPublish: false</c> that is harmless today, but the pairing is
    /// exactly the kind of thing a LiveKit upgrade changes, so the list always names something and
    /// <c>canPublish</c> is what actually says no.
    /// </remarks>
    public static IReadOnlyList<string> PublishableSources(VoicePublishGrant grant)
    {
        var sources = new List<string>(4);

        if (grant.Microphone)
            sources.Add(MicrophoneSource);
        if (grant.Camera)
            sources.Add(CameraSource);
        if (grant.Screen)
            sources.Add(ScreenShareSource);
        // A window's sound only ever travels with its picture; a grant that somehow names one without
        // the other gets neither, rather than a sound track with nothing to belong to.
        if (grant.Screen && grant.ScreenAudio)
            sources.Add(ScreenShareAudioSource);

        return sources.Count == 0 ? [MicrophoneSource] : sources;
    }

    /// <summary>LiveKit's names for the sources, as a join token's <c>canPublishSources</c> carries them.</summary>
    public const string MicrophoneSource = "microphone";

    /// <inheritdoc cref="MicrophoneSource"/>
    public const string CameraSource = "camera";

    /// <inheritdoc cref="MicrophoneSource"/>
    public const string ScreenShareSource = "screen_share";

    /// <inheritdoc cref="MicrophoneSource"/>
    public const string ScreenShareAudioSource = "screen_share_audio";

    /// <summary>A token for Raindrop's own call to the room API, scoped to <paramref name="room"/> when given.</summary>
    public string CreateAdminToken(string? room)
    {
        var now = _time.GetUtcNow();
        var video = new JsonObject { ["roomList"] = true, ["roomCreate"] = true };

        if (room is not null)
        {
            video["roomAdmin"] = true;
            video["room"] = room;
        }

        return Sign(new JsonObject
        {
            ["iss"] = _options.ApiKey,
            ["sub"] = "raindrop-server",
            ["nbf"] = (now - Skew).ToUnixTimeSeconds(),
            ["exp"] = (now + AdminLifetime).ToUnixTimeSeconds(),
            ["video"] = video
        });
    }

    /// <summary>
    /// Checks a token a client presents — one Raindrop minted, or the refreshed one LiveKit hands a
    /// connected participant, which it signs with the same key. Returns who it admits to which room, or
    /// null when it is forged, expired, for another key, or not a join token at all.
    /// </summary>
    public (string Identity, string Room)? TryValidateParticipantToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        var parts = token.Split('.');
        if (parts.Length != 3)
            return null;

        try
        {
            var header = JsonNode.Parse(Base64UrlDecode(parts[0]));
            if (header?["alg"]?.GetValue<string>() != "HS256")
                return null;

            var expected = HMACSHA256.HashData(_secret, Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]));
            if (!CryptographicOperations.FixedTimeEquals(expected, Base64UrlDecode(parts[2])))
                return null;

            var claims = JsonNode.Parse(Base64UrlDecode(parts[1]))?.AsObject();
            if (claims is null || claims["iss"]?.GetValue<string>() != _options.ApiKey)
                return null;

            var now = _time.GetUtcNow().ToUnixTimeSeconds();
            var skew = (long)Skew.TotalSeconds;

            if (claims["exp"] is not { } exp || exp.GetValue<long>() + skew < now)
                return null;

            if (claims["nbf"] is { } nbf && nbf.GetValue<long>() - skew > now)
                return null;

            var identity = claims["sub"]?.GetValue<string>();
            var video = claims["video"]?.AsObject();
            var room = video?["room"]?.GetValue<string>();

            if (string.IsNullOrEmpty(identity) || room is null || video?["roomJoin"]?.GetValue<bool>() != true)
                return null;

            return (identity, room);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private string Sign(JsonObject claims)
    {
        var header = Base64UrlEncode("""{"alg":"HS256","typ":"JWT"}"""u8.ToArray());
        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(claims));
        var signature = HMACSHA256.HashData(_secret, Encoding.ASCII.GetBytes(header + "." + payload));

        return header + "." + payload + "." + Base64UrlEncode(signature);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(padded);
    }
}
