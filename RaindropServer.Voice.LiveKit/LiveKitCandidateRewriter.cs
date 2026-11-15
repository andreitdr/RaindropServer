using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using RaindropServer.Voice.LiveKit.Protocol;

namespace RaindropServer.Voice.LiveKit;

/// <summary>
/// Points the ICE candidates LiveKit sends a client at the address that client reached Raindrop by,
/// wherever LiveKit advertised a loopback address.
/// </summary>
/// <remarks>
/// LiveKit runs in a container and cannot see the address its host is reached by, so it advertises
/// whatever <c>--node-ip</c> says, 127.0.0.1 by default. That is right for a client on the same machine
/// and unreachable for every other one: a phone would send its audio to its own loopback, and the call
/// would never connect. The relay knows better. A client that reached Raindrop at an address can reach
/// LiveKit's published media ports there too, since both run on the same host — which is exactly what a
/// loopback node IP says. Candidates on any other address were configured on purpose (a public IP, a
/// separate media host) and are left alone, as is every message that carries no candidate.
/// </remarks>
internal static class LiveKitCandidateRewriter
{
    private const string SdpCandidatePrefix = "a=candidate:";

    /// <summary>
    /// The message with its loopback candidates pointed at <paramref name="address"/>, or null when there
    /// was nothing to change (the caller then forwards the original bytes untouched).
    /// </summary>
    public static byte[]? Rewrite(ReadOnlySpan<byte> message, IPAddress address)
    {
        SignalResponse response;
        try
        {
            response = SignalResponse.Parser.ParseFrom(message);
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }

        var target = address.ToString();
        var changed = response.MessageCase switch
        {
            SignalResponse.MessageOneofCase.Answer => RewriteDescription(response.Answer, target),
            SignalResponse.MessageOneofCase.Offer => RewriteDescription(response.Offer, target),
            SignalResponse.MessageOneofCase.Trickle => RewriteTrickle(response.Trickle, target),
            _ => false
        };

        return changed ? response.ToByteArray() : null;
    }

    private static bool RewriteDescription(SessionDescription description, string address)
    {
        var lines = description.Sdp.Split('\n');
        var changed = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var ending = line.EndsWith('\r') ? "\r" : "";
            var content = line.AsSpan(0, line.Length - ending.Length);

            if (!content.StartsWith(SdpCandidatePrefix, StringComparison.Ordinal))
                continue;

            if (RewriteCandidate(content.ToString(), address) is { } rewritten)
            {
                lines[i] = rewritten + ending;
                changed = true;
            }
        }

        if (changed)
            description.Sdp = string.Join('\n', lines);

        return changed;
    }

    private static bool RewriteTrickle(TrickleRequest trickle, string address)
    {
        JsonObject? init;
        try
        {
            init = JsonNode.Parse(trickle.CandidateInit) as JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }

        if (init?["candidate"] is not JsonValue value || !value.TryGetValue<string>(out var candidate))
            return false;

        if (RewriteCandidate(candidate, address) is not { } rewritten)
            return false;

        init["candidate"] = rewritten;
        trickle.CandidateInit = init.ToJsonString();
        return true;
    }

    /// <summary>
    /// One candidate line, <c>[a=]candidate:foundation component transport priority address port typ …</c>
    /// (RFC 8839 §5.1), with its address replaced when that address is loopback; else null.
    /// </summary>
    internal static string? RewriteCandidate(string candidate, string address)
    {
        var fields = candidate.Split(' ');
        if (fields.Length < 8 || !IPAddress.TryParse(fields[4], out var advertised) || !IPAddress.IsLoopback(advertised))
            return null;

        fields[4] = address;
        return string.Join(' ', fields);
    }
}
