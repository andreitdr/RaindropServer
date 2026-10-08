using System.Diagnostics;
using System.Threading.Channels;
using Grpc.Core;
using RaindropServer.Events;
using RaindropServer.Users;
using Proto = RaindropServer.Grpc.Protocol;

namespace RaindropServer.Grpc.Events;

/// <summary>
/// One client's <c>EventService.Connect</c> stream as an <see cref="IEventConnection"/>: a bounded queue
/// of frames going out, and heartbeats and resubscribes coming in.
/// </summary>
/// <remarks>
/// A gRPC stream allows one write in flight at a time, so everything goes out on the one send loop;
/// everything else asks for a close through <see cref="Close"/>, and the loop ends the call with the
/// status that reason maps to.
/// </remarks>
public sealed class GrpcEventConnection : IEventConnection
{
    private readonly EventOptions _options;
    private readonly Channel<object> _outbox;
    private readonly CancellationTokenSource _closing = new();
    private long _lastHeartbeat = Stopwatch.GetTimestamp();
    private int _closeRequested;
    private EventCloseReason _closeReason = EventCloseReason.Normal;

    public GrpcEventConnection(UserSession session, IEventEncoder encoder, EventOptions options)
    {
        Session = session;
        Encoder = encoder;
        _options = options;
        _outbox = Channel.CreateBounded<object>(new BoundedChannelOptions(options.MaximumPendingEvents)
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
    public IEventEncoder Encoder { get; }

    /// <inheritdoc />
    public void Deliver(object frame)
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

    /// <summary>
    /// Pumps the stream until it closes, then ends the call: OK when the client finished it, otherwise a
    /// status whose <c>ErrorInfo.reason</c> says why.
    /// </summary>
    public async Task RunAsync(
        IAsyncStreamReader<Proto.ConnectRequest> requests,
        IServerStreamWriter<Proto.ConnectResponse> responses,
        IEventConnectionRegistry registry,
        CancellationToken aborted)
    {
        using var abortRegistration = aborted.Register(() => Close(EventCloseReason.ConnectionLost));

        // The read waits on the client, so it has to give up when the connection closes for any other
        // reason too — the call cannot end while a read is still pending on it.
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(aborted, _closing.Token);

        var receiving = ReceiveAsync(requests, registry, reading.Token);
        var watching = WatchHeartbeatsAsync();

        await SendAsync(responses, aborted);

        // Sending only stops once the connection is closing, which ends the other two as well.
        await Task.WhenAll(watching, receiving);

        if (Ending() is { } ending)
            throw ending;
    }

    private async Task SendAsync(IServerStreamWriter<Proto.ConnectResponse> responses, CancellationToken aborted)
    {
        var reader = _outbox.Reader;

        try
        {
            // No token on the wait: Close completes the queue, which is what ends it.
            while (await reader.WaitToReadAsync())
            {
                while (!_closing.IsCancellationRequested && reader.TryRead(out var frame))
                    await responses.WriteAsync((Proto.ConnectResponse)frame, aborted);

                if (_closing.IsCancellationRequested)
                    return;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            Close(EventCloseReason.ConnectionLost);
        }
    }

    /// <summary>
    /// Reads the client's half: heartbeats keep the stream alive, a resubscribe asks for a fresh ready,
    /// and anything else is a client bug. The client finishing its half ends the stream normally.
    /// </summary>
    private async Task ReceiveAsync(
        IAsyncStreamReader<Proto.ConnectRequest> requests, IEventConnectionRegistry registry, CancellationToken aborted)
    {
        try
        {
            while (await requests.MoveNext(aborted))
            {
                switch (requests.Current.FrameCase)
                {
                    case Proto.ConnectRequest.FrameOneofCase.Heartbeat:
                        Interlocked.Exchange(ref _lastHeartbeat, Stopwatch.GetTimestamp());
                        break;

                    case Proto.ConnectRequest.FrameOneofCase.Resubscribe:
                        Interlocked.Exchange(ref _lastHeartbeat, Stopwatch.GetTimestamp());
                        registry.Resubscribe(this);
                        break;

                    default:
                        Close(EventCloseReason.ProtocolViolation);
                        return;
                }
            }

            Close(EventCloseReason.Normal);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            Close(EventCloseReason.ConnectionLost);
        }
    }

    /// <summary>Closes the stream as lost once no heartbeat has arrived for the timeout.</summary>
    private async Task WatchHeartbeatsAsync()
    {
        var period = _options.HeartbeatTimeout / 4;
        using var timer = new PeriodicTimer(period < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : period);

        try
        {
            while (await timer.WaitForNextTickAsync(_closing.Token))
            {
                if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastHeartbeat)) > _options.HeartbeatTimeout)
                {
                    Close(EventCloseReason.ConnectionLost);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>How the call ends for the reason it closed; null for a normal end.</summary>
    private RpcException? Ending() => _closeReason switch
    {
        EventCloseReason.SessionEnded => RaindropInterceptor.ToRpcException(
            StatusCode.Unauthenticated, "session_ended", "The session behind this stream ended; log in again."),
        EventCloseReason.TooSlow => RaindropInterceptor.ToRpcException(
            StatusCode.ResourceExhausted, "too_slow", "This stream fell too far behind; reconnect and re-read."),
        EventCloseReason.ServerStopping => RaindropInterceptor.ToRpcException(
            StatusCode.Unavailable, "server_stopping", "The server is stopping."),
        EventCloseReason.ConnectionLost => RaindropInterceptor.ToRpcException(
            StatusCode.Unavailable, "connection_lost", "No heartbeat arrived in time; reconnect."),
        EventCloseReason.ProtocolViolation => RaindropInterceptor.ToRpcException(
            StatusCode.InvalidArgument, "invalid_frame", "This stream accepts only heartbeats and resubscribes."),
        _ => null
    };
}
