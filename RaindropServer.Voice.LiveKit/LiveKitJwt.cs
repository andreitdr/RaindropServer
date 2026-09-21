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
    /// A token that lets one member into one room. The microphone is the only thing they may publish,
    /// and only when <see cref="VoiceGrant.CanSpeak"/>; they cannot send data or rewrite their own
    /// metadata, so nothing about them in the call is theirs to forge.
    /// </summary>
    public (string Token, DateTime ExpiresAtUtc) CreateParticipantToken(VoiceGrant grant)
    {
        var now = _time.GetUtcNow();
        var expires = now + _options.TokenLifetime;
        var identity = grant.UserId.ToString("D");

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
                ["canPublish"] = grant.CanSpeak,
                ["canPublishData"] = false,
                ["canPublishSources"] = new JsonArray("microphone"),
                ["canUpdateOwnMetadata"] = false
            }
        };

        return (Sign(claims), expires.UtcDateTime);
    }

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
