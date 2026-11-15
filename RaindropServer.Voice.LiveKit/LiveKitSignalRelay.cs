using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using RaindropServer.Channels.Voice;
using RaindropServer.Users;

namespace RaindropServer.Voice.LiveKit;

/// <summary>
/// Forwards a client's LiveKit signalling connection from <c>/api/channels/voice/signal/rtc</c> to the
/// LiveKit server, so that connection rides Raindrop's own pinned TLS certificate and LiveKit's HTTP
/// port never has to face the internet.
/// </summary>
/// <remarks>
/// It is also a gate. A connection is only forwarded for a token that Raindrop (or LiveKit, refreshing
/// one Raindrop minted) signed, naming a member Raindrop has admitted to that very call on a session
/// that is still live. A member removed from a call cannot use their unexpired token to walk back in
/// this way, whatever LiveKit itself would allow.
/// <para>
/// Only LiveKit's signalling paths are forwarded — never its room API.
/// </para>
/// <para>
/// What LiveKit sends the client is forwarded as it came, except for one thing: ICE candidates on a
/// loopback address are pointed at the address the client reached Raindrop by
/// (<see cref="LiveKitCandidateRewriter"/>), so a LiveKit container on the same host needs no address
/// configured for phones and other computers to reach it.
/// </para>
/// </remarks>
public sealed class LiveKitSignalRelay : IVoiceSignalRelay, IDisposable
{
    private static readonly HashSet<string> Forwarded = new(StringComparer.Ordinal)
    {
        "rtc", "rtc/validate", "rtc/v1", "rtc/v1/validate"
    };

    private readonly LiveKitOptions _options;
    private readonly LiveKitJwt _jwt;
    private readonly IVoiceStateStore _state;
    private readonly ISessionStore _sessions;
    private readonly IVoiceSignalMonitor? _monitor;
    private readonly CancellationToken _stopping;
    private readonly HttpClient _http;

    public LiveKitSignalRelay(
        LiveKitOptions options,
        LiveKitJwt jwt,
        IVoiceStateStore state,
        ISessionStore sessions,
        IHostApplicationLifetime? lifetime = null,
        IVoiceSignalMonitor? monitor = null)
    {
        _options = options;
        _jwt = jwt;
        _state = state;
        _sessions = sessions;
        _monitor = monitor;
        _stopping = lifetime?.ApplicationStopping ?? CancellationToken.None;
        _http = new HttpClient { BaseAddress = options.ApiBase, Timeout = options.RequestTimeout };
    }

    public async Task RelayAsync(HttpContext context, string path, CancellationToken cancellationToken)
    {
        path = path.Trim('/');

        if (!Forwarded.Contains(path))
        {
            await WriteTextAsync(context, StatusCodes.Status404NotFound, "Not a voice signalling path.", cancellationToken);
            return;
        }

        var (token, fromHeader) = TokenOf(context.Request);

        if (await AdmittedAsync(token, cancellationToken) is not { } admitted)
        {
            await WriteTextAsync(context, StatusCodes.Status401Unauthorized,
                "This server has not admitted you to that call. Join it again.", cancellationToken);
            return;
        }

        var target = path + context.Request.QueryString;

        if (context.WebSockets.IsWebSocketRequest)
            await RelayWebSocketAsync(context, target, fromHeader ? token : null, admitted, cancellationToken);
        else
            await RelayHttpAsync(context, target, fromHeader ? token : null, cancellationToken);
    }

    public void Dispose() => _http.Dispose();

    // ── Admission ────────────────────────────────────────────────────────────

    /// <summary>
    /// Native clients send the token as a bearer header; a browser cannot set headers on a WebSocket,
    /// so LiveKit's web client puts it in the query instead.
    /// </summary>
    private static (string? Token, bool FromHeader) TokenOf(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return (header["Bearer ".Length..].Trim(), true);

        return (request.Query["access_token"].ToString(), false);
    }

    /// <summary>The call and member a token is for, when Raindrop has admitted that member to it; else null.</summary>
    private async Task<(Guid ChannelId, Guid UserId)?> AdmittedAsync(string? token, CancellationToken cancellationToken)
    {
        if (_jwt.TryValidateParticipantToken(token) is not { } claims)
            return null;

        if (!Guid.TryParse(claims.Identity, out var userId) || _jwt.ChannelFor(claims.Room) is not { } channelId)
            return null;

        var record = await _state.FindAsync(userId, cancellationToken);
        return record is not null && record.ChannelId == channelId && _sessions.IsActive(record.SessionToken)
            ? (channelId, userId)
            : null;
    }

    // ── Forwarding ───────────────────────────────────────────────────────────

    private async Task RelayWebSocketAsync(
        HttpContext context, string target, string? bearer, (Guid ChannelId, Guid UserId) member,
        CancellationToken cancellationToken)
    {
        using var upstream = new ClientWebSocket();
        upstream.Options.CollectHttpResponseDetails = true;
        if (bearer is not null)
            upstream.Options.SetRequestHeader("Authorization", "Bearer " + bearer);

        // Upstream first, so that LiveKit refusing the connection reaches the client as the HTTP status
        // it would have seen talking to LiveKit directly, rather than as an accepted socket that dies.
        try
        {
            using var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping);
            connecting.CancelAfter(_options.RequestTimeout);
            await upstream.ConnectAsync(new Uri(WebSocketBase(), target), connecting.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            var status = upstream.HttpStatusCode is 0 ? StatusCodes.Status502BadGateway : (int)upstream.HttpStatusCode;
            await WriteTextAsync(context, status, "The voice server did not accept the connection.", cancellationToken);
            return;
        }

        var reachedAt = await ReachedAtAsync(context, cancellationToken);

        using var downstream = await context.WebSockets.AcceptWebSocketAsync();
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping);

        // However this ends — a clean leave, a network drop, or the client's process dying and the OS
        // closing its socket — the voice service hears of it, and drops a member who does not come
        // back within its grace period.
        _monitor?.SignalOpened(member.ChannelId, member.UserId);
        try
        {
            var toLiveKit = PumpAsync(downstream, upstream, session.Token);
            var toClient = reachedAt is null
                ? PumpAsync(upstream, downstream, session.Token)
                : PumpRewritingAsync(upstream, downstream, reachedAt, session.Token);

            await Task.WhenAny(toLiveKit, toClient);
            await session.CancelAsync();
            await Task.WhenAll(toLiveKit, toClient);
        }
        finally
        {
            Abandon(downstream);
            Abandon(upstream);
            _monitor?.SignalClosed(member.ChannelId, member.UserId);
        }
    }

    /// <summary>
    /// Copies every frame from one socket to the other until either end closes, passing the close
    /// on. Fragments are forwarded as they arrive, so a message larger than the buffer is not
    /// reassembled here.
    /// </summary>
    private static async Task PumpAsync(WebSocket source, WebSocket destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await source.ReceiveAsync(buffer, cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    var status = source.CloseStatus is null or WebSocketCloseStatus.Empty
                        ? WebSocketCloseStatus.NormalClosure
                        : source.CloseStatus.Value;

                    if (destination.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        await destination.CloseOutputAsync(status, source.CloseStatusDescription, cancellationToken);

                    return;
                }

                await destination.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage,
                    cancellationToken);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // One side dropped or the relay is shutting down; the caller tears both down.
        }
    }

    /// <summary>
    /// Like <see cref="PumpAsync"/>, but each whole binary message is passed through
    /// <see cref="LiveKitCandidateRewriter"/> before it is sent on. A message larger than
    /// <see cref="MaximumRewrittenMessageBytes"/> carries no SDP worth rewriting, and is streamed on as
    /// it arrives rather than held.
    /// </summary>
    private static async Task PumpRewritingAsync(
        WebSocket source, WebSocket destination, IPAddress reachedAt, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var message = new ArrayBufferWriter<byte>();
        var streaming = false;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await source.ReceiveAsync(buffer, cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    var status = source.CloseStatus is null or WebSocketCloseStatus.Empty
                        ? WebSocketCloseStatus.NormalClosure
                        : source.CloseStatus.Value;

                    if (destination.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        await destination.CloseOutputAsync(status, source.CloseStatusDescription, cancellationToken);

                    return;
                }

                var chunk = buffer.AsMemory(0, result.Count);

                if (streaming || result.MessageType != WebSocketMessageType.Binary)
                {
                    await destination.SendAsync(chunk, result.MessageType, result.EndOfMessage, cancellationToken);
                    streaming = !result.EndOfMessage;
                    continue;
                }

                message.Write(chunk.Span);

                if (!result.EndOfMessage)
                {
                    if (message.WrittenCount <= MaximumRewrittenMessageBytes)
                        continue;

                    // Too large to be a description: send what is held and stream the rest.
                    await destination.SendAsync(message.WrittenMemory, WebSocketMessageType.Binary, false,
                        cancellationToken);
                    message.Clear();
                    streaming = true;
                    continue;
                }

                var outgoing = LiveKitCandidateRewriter.Rewrite(message.WrittenSpan, reachedAt) is { } rewritten
                    ? rewritten
                    : message.WrittenMemory;

                await destination.SendAsync(outgoing, WebSocketMessageType.Binary, true, cancellationToken);
                message.Clear();
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // One side dropped or the relay is shutting down; the caller tears both down.
        }
    }

    /// <summary>The largest signalling message held whole to be rewritten. An SDP is a few kilobytes.</summary>
    private const int MaximumRewrittenMessageBytes = 1024 * 1024;

    /// <summary>
    /// The address this client reached Raindrop by, from the host it asked for: an address as it is, a
    /// name resolved here. Null when that is loopback itself (nothing to rewrite: the client is on this
    /// machine) or cannot be worked out, in which case LiveKit's candidates are forwarded untouched.
    /// </summary>
    private static async Task<IPAddress?> ReachedAtAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var host = context.Request.Host.Host.Trim('[', ']');
        if (host.Length == 0)
            return null;

        if (!IPAddress.TryParse(host, out var address))
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
                address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                          ?? addresses.FirstOrDefault();
            }
            catch (SocketException)
            {
                return null;
            }
        }

        if (address is null || IPAddress.IsLoopback(address))
            return null;

        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    private static void Abandon(WebSocket socket)
    {
        if (socket.State is not (WebSocketState.Closed or WebSocketState.Aborted))
            socket.Abort();
    }

    /// <summary>
    /// LiveKit's <c>/rtc/validate</c>, which a client calls after a failed connection to learn why —
    /// passed through untouched, status and body both.
    /// </summary>
    private async Task RelayHttpAsync(HttpContext context, string target, string? bearer, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            await WriteTextAsync(context, StatusCodes.Status502BadGateway, "The voice server is unreachable.", cancellationToken);
            return;
        }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is { } contentType)
                context.Response.ContentType = contentType.ToString();

            await response.Content.CopyToAsync(context.Response.Body, cancellationToken);
        }
    }

    private Uri WebSocketBase()
    {
        var builder = new UriBuilder(_options.ApiBase);
        builder.Scheme = builder.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        return builder.Uri;
    }

    /// <summary>
    /// Plain text rather than a gRPC status: the caller is LiveKit's client library, which
    /// shows the body of a refused validate call as the reason.
    /// </summary>
    private static Task WriteTextAsync(HttpContext context, int status, string message, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(message, cancellationToken);
    }
}
