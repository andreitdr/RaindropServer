using System.Net.WebSockets;
using System.Threading.Channels;
using RaindropServer.Payloads;
using RaindropServer.Users;

namespace RaindropServer.Events.WebSockets;

/// <summary>
/// An <see cref="IEventConnection"/> over a WebSocket, with a bounded queue of frames waiting to go
/// out. JSON is sent in text frames and MessagePack in binary ones, so a client can tell them apart
/// without asking.
/// </summary>
/// <remarks>
/// A WebSocket allows one send and one receive in flight at a time, so every send — the close frame
/// included — happens on the one send loop, and everything else asks for a close through
/// <see cref="Close"/> rather than touching the socket itself.
/// </remarks>
public sealed class WebSocketEventConnection : IEventConnection
{
    private readonly WebSocket _socket;
    private readonly EventOptions _options;
    private readonly Channel<byte[]> _outbox;

    // Never disposed: the registry may still ask a finished connection to close, and a plain
    // CancellationTokenSource holds nothing that needs releasing.
    private readonly CancellationTokenSource _closing = new();

    private int _closeRequested;
    private EventCloseReason _closeReason = EventCloseReason.Normal;

    public WebSocketEventConnection(UserSession session, IPayloadCodec codec, WebSocket socket, EventOptions options)
    {
        Session = session;
        Codec = codec;
        _socket = socket;
        _options = options;
        _outbox = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(options.MaximumPendingEvents)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    /// <inheritdoc />
    public Guid Id { get; } = Guid.NewGuid();

    /// <inheritdoc />
    public UserSession Session { get; }

    /// <inheritdoc />
    public IPayloadCodec Codec { get; }

    /// <inheritdoc />
    public void Deliver(byte[] frame)
    {
        // A client this far behind is better off reconnecting and re-reading the current state than
        // receiving a backlog nobody will look at.
        if (!_outbox.Writer.TryWrite(frame))
            Close(EventCloseReason.TooSlow);
    }

    /// <inheritdoc />
    public void Close(EventCloseReason reason)
    {
        if (Interlocked.Exchange(ref _closeRequested, 1) != 0)
            return;

        _closeReason = reason;
        _outbox.Writer.TryComplete();
        _closing.Cancel();
    }

    /// <summary>Pumps the connection until it closes, for whatever reason.</summary>
    /// <param name="aborted">Signalled when the underlying request is torn down.</param>
    public async Task RunAsync(CancellationToken aborted)
    {
        using var abortRegistration = aborted.Register(() => Close(EventCloseReason.ConnectionLost));

        var receiving = ReceiveAsync(aborted);

        await SendAsync(aborted);
        await CloseHandshakeAsync(receiving);
    }

    private async Task SendAsync(CancellationToken aborted)
    {
        var type = Codec.IsBinary ? WebSocketMessageType.Binary : WebSocketMessageType.Text;
        var reader = _outbox.Reader;

        try
        {
            // No cancellation token on the wait: Close completes the queue, which is what ends it.
            // Cancelling an in-flight send would abort the socket and lose the close code with it,
            // so sends only give up when the request itself is gone.
            while (await reader.WaitToReadAsync())
            {
                while (!_closing.IsCancellationRequested && reader.TryRead(out var frame))
                    await _socket.SendAsync(frame, type, endOfMessage: true, aborted);

                if (_closing.IsCancellationRequested)
                    return;
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            Close(EventCloseReason.ConnectionLost);
        }
    }

    /// <summary>
    /// Reads until the client closes. Clients have nothing to say on this stream — requests go
    /// through the REST API — so anything but a close frame is refused.
    /// </summary>
    private async Task ReceiveAsync(CancellationToken aborted)
    {
        var buffer = new byte[512];

        try
        {
            while (_socket.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                var result = await _socket.ReceiveAsync(buffer.AsMemory(), aborted);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Close(EventCloseReason.Normal);
                    return;
                }

                // Keep reading afterwards: the client's close frame is still to come.
                Close(EventCloseReason.ProtocolViolation);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            Close(EventCloseReason.ConnectionLost);
        }
    }

    private async Task CloseHandshakeAsync(Task receiving)
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            using var timeout = new CancellationTokenSource(_options.CloseTimeout);
            var (status, description) = WebSocketCloseCodes.For(_closeReason);

            try
            {
                await _socket.CloseOutputAsync(status, description, timeout.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                // The client is already gone; there is nobody to tell.
            }
        }

        // Give the client a moment to answer with its own close frame, which ends the receive loop,
        // but do not hold the request open for a client that never does.
        if (await Task.WhenAny(receiving, Task.Delay(_options.CloseTimeout)) != receiving)
            _socket.Abort();

        await receiving;
    }
}

/// <summary>
/// How each <see cref="EventCloseReason"/> is spelled on a WebSocket. The standard codes are used
/// where one fits; the rest sit in the 4000–4999 range the protocol leaves to applications, numbered
/// after the HTTP status that means the same thing.
/// </summary>
public static class WebSocketCloseCodes
{
    /// <summary>The session ended. Mirrors HTTP 401: log in again, then reconnect.</summary>
    public const int SessionEnded = 4401;

    /// <summary>The client fell too far behind. Mirrors HTTP 429: reconnect and resynchronise.</summary>
    public const int TooSlow = 4429;

    /// <summary>The close status and the short machine-readable description sent with it.</summary>
    public static (WebSocketCloseStatus Status, string Description) For(EventCloseReason reason) => reason switch
    {
        EventCloseReason.SessionEnded => ((WebSocketCloseStatus)SessionEnded, "session_ended"),
        EventCloseReason.TooSlow => ((WebSocketCloseStatus)TooSlow, "too_slow"),
        EventCloseReason.ServerStopping => (WebSocketCloseStatus.EndpointUnavailable, "server_stopping"),
        EventCloseReason.ProtocolViolation => (WebSocketCloseStatus.PolicyViolation, "server_events_only"),
        EventCloseReason.ConnectionLost => (WebSocketCloseStatus.EndpointUnavailable, "connection_lost"),
        _ => (WebSocketCloseStatus.NormalClosure, "closed")
    };
}
