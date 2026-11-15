using System.Net;
using System.Text.Json.Nodes;
using Google.Protobuf;
using RaindropServer.Voice.LiveKit;
using RaindropServer.Voice.LiveKit.Protocol;

namespace RaindropServer.Tests;

/// <summary>
/// The relay's one change to LiveKit's signalling: loopback ICE candidates pointed at the address the
/// client reached Raindrop by, and nothing else touched.
/// </summary>
public class LiveKitCandidateRewriterTests
{
    private static readonly IPAddress Lan = IPAddress.Parse("192.168.0.236");

    private const string Sdp =
        "v=0\r\n" +
        "o=- 1 2 IN IP4 127.0.0.1\r\n" +
        "m=audio 9 UDP/TLS/RTP/SAVPF 111\r\n" +
        "a=candidate:1 1 udp 2130706431 127.0.0.1 7882 typ host generation 0\r\n" +
        "a=candidate:2 1 tcp 1671430143 127.0.0.1 7881 typ host tcptype passive generation 0\r\n" +
        "a=candidate:3 1 udp 2130706431 203.0.113.9 7882 typ host generation 0\r\n" +
        "a=end-of-candidates\r\n";

    [Fact]
    public void Rewrites_loopback_candidates_in_an_offer_and_keeps_every_other_line()
    {
        var response = new SignalResponse { Offer = new SessionDescription { Type = "offer", Sdp = Sdp } };

        var rewritten = LiveKitCandidateRewriter.Rewrite(response.ToByteArray(), Lan);

        Assert.NotNull(rewritten);
        var sdp = SignalResponse.Parser.ParseFrom(rewritten).Offer.Sdp;
        Assert.Equal(Sdp
            .Replace("2130706431 127.0.0.1 7882", "2130706431 192.168.0.236 7882")
            .Replace("1671430143 127.0.0.1 7881", "1671430143 192.168.0.236 7881"), sdp);
        // The origin line is not a candidate, and a deliberately configured address is left alone.
        Assert.Contains("o=- 1 2 IN IP4 127.0.0.1\r\n", sdp);
        Assert.Contains("203.0.113.9 7882", sdp);
    }

    [Fact]
    public void Rewrites_an_answer()
    {
        var response = new SignalResponse { Answer = new SessionDescription { Type = "answer", Sdp = Sdp } };

        var rewritten = LiveKitCandidateRewriter.Rewrite(response.ToByteArray(), Lan);

        Assert.NotNull(rewritten);
        Assert.DoesNotContain("127.0.0.1 788", SignalResponse.Parser.ParseFrom(rewritten).Answer.Sdp);
    }

    [Fact]
    public void Rewrites_a_trickled_candidate_and_keeps_the_fields_this_copy_does_not_know()
    {
        const string init = """{"candidate":"candidate:1 1 udp 2130706431 ::1 7882 typ host","sdpMid":"0","sdpMLineIndex":0}""";
        // TrickleRequest.target (2) = SUBSCRIBER, which the trimmed proto leaves out.
        var bytes = Signal(4, Concat(Field(1, init), [0x10, 0x01]));

        var rewritten = LiveKitCandidateRewriter.Rewrite(bytes, Lan);

        Assert.NotNull(rewritten);
        var trickle = SignalResponse.Parser.ParseFrom(rewritten).Trickle;
        var json = JsonNode.Parse(trickle.CandidateInit)!;
        Assert.Equal("candidate:1 1 udp 2130706431 192.168.0.236 7882 typ host", (string)json["candidate"]!);
        Assert.Equal("0", (string)json["sdpMid"]!);
        Assert.Equal(0, (int)json["sdpMLineIndex"]!);
        Assert.Equal(new byte[] { 0x10, 0x01 }, rewritten[^2..]);
    }

    [Fact]
    public void Leaves_a_message_with_nothing_to_rewrite_untouched()
    {
        var response = new SignalResponse
        {
            Offer = new SessionDescription { Type = "offer", Sdp = "a=candidate:3 1 udp 1 203.0.113.9 7882 typ host\r\n" }
        };

        Assert.Null(LiveKitCandidateRewriter.Rewrite(response.ToByteArray(), Lan));
    }

    [Fact]
    public void Leaves_messages_it_does_not_know_and_bytes_that_are_not_protobuf_untouched()
    {
        // SignalResponse.update (5), a case the trimmed proto does not have.
        Assert.Null(LiveKitCandidateRewriter.Rewrite(Signal(5, [0x0a, 0x00]), Lan));
        Assert.Null(LiveKitCandidateRewriter.Rewrite([0xff, 0xff, 0xff], Lan));
    }

    [Theory]
    [InlineData("candidate:1 1 udp 1 127.0.0.1 7882 typ host", "candidate:1 1 udp 1 10.0.0.2 7882 typ host")]
    [InlineData("a=candidate:1 1 udp 1 127.5.5.5 7882 typ host", "a=candidate:1 1 udp 1 10.0.0.2 7882 typ host")]
    [InlineData("candidate:1 1 udp 1 172.18.0.3 7882 typ host", null)]
    [InlineData("candidate:1 1 udp 1 host.local 7882 typ host", null)]
    [InlineData("candidate:garbled", null)]
    public void Rewrites_only_a_loopback_address_field(string candidate, string? expected) =>
        Assert.Equal(expected, LiveKitCandidateRewriter.RewriteCandidate(candidate, "10.0.0.2"));

    private static byte[] Signal(int field, byte[] body) => Concat([(byte)(field << 3 | 2), (byte)body.Length], body);

    private static byte[] Field(int field, string value)
    {
        var text = System.Text.Encoding.UTF8.GetBytes(value);
        return Concat([(byte)(field << 3 | 2), (byte)text.Length], text);
    }

    private static byte[] Concat(byte[] a, byte[] b) => [.. a, .. b];
}
