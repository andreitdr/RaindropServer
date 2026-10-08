using RaindropServer.Users;

namespace RaindropServer.Events;

/// <summary>
/// One client listening for events, whatever carries them. The gRPC event stream is the transport this
/// server ships (<c>RaindropServer.Grpc</c>); another — a push gateway, say — is added by implementing
/// this and registering its connections with <see cref="IEventConnectionRegistry"/>, and nothing that
/// publishes has to change.
/// </summary>
/// <remarks>
/// Implementations must not block in either method: they are called from inside a publish, on the
/// thread of whichever request caused the event. Buffer the frame and send it on your own time, or
/// close the connection if the buffer is full.
/// </remarks>
public interface IEventConnection
{
    /// <summary>Unique among the connections open right now.</summary>
    Guid Id { get; }

    /// <summary>
    /// The session that opened the connection. It decides who the connection belongs to — and so what
    /// it may hear — and the connection is closed when the session ends.
    /// </summary>
    UserSession Session { get; }

    /// <summary>
    /// How events are turned into this transport's frames. Connections sharing an encoder share each
    /// encoded frame, so an event is encoded once however many of them hear it.
    /// </summary>
    IEventEncoder Encoder { get; }

    /// <summary>Queues one frame <see cref="Encoder"/> produced. Must not block.</summary>
    void Deliver(object frame);

    /// <summary>
    /// Asks for the connection to be closed. Only the first request counts, so the reason a client is
    /// given is the reason its connection actually ended. Must not block.
    /// </summary>
    void Close(EventCloseReason reason);
}

/// <summary>
/// Turns a <see cref="ServerEvent{T}"/> into whatever a transport sends: for the gRPC stream, one
/// protobuf message. The result is shared between connections, so it must not be changed afterwards.
/// </summary>
public interface IEventEncoder
{
    /// <summary>
    /// The frame for <paramref name="serverEvent"/>, or null when this transport cannot carry it — a
    /// plugin's event whose payload it has no encoding for — in which case its connections do not
    /// receive that event.
    /// </summary>
    object? Encode<T>(ServerEvent<T> serverEvent);
}

/// <summary>
/// Where transports hand over the connections they open, so publishers can reach them. The registry
/// sends <see cref="ServerEventTypes.Ready"/> first, closes a connection whose session ends, and
/// closes them all when the server stops.
/// </summary>
public interface IEventConnectionRegistry
{
    /// <summary>How many connections are open right now.</summary>
    int Count { get; }

    /// <summary>
    /// Makes a connection reachable. <see cref="ServerEventTypes.Ready"/> is delivered to it before it
    /// can receive anything else. Dispose the result once the connection has closed.
    /// </summary>
    IDisposable Register(IEventConnection connection);

    /// <summary>
    /// Delivers a fresh <see cref="ServerEventTypes.Ready"/> to a registered connection, which its client
    /// takes as the cue to re-read everything — what a client asks for after it was suspended, rather
    /// than reconnecting.
    /// </summary>
    void Resubscribe(IEventConnection connection);

    /// <summary>The members with at least one connection open right now.</summary>
    IReadOnlySet<Guid> ConnectedUserIds();
}

/// <summary>
/// Told when a member's first event connection opens or their last one closes — the moment they start
/// or stop listening anywhere. Register with <c>TryAddEnumerable</c>; presence is what uses it.
/// </summary>
public interface IEventStreamListener
{
    /// <summary>Called after the change; ask the registry for the state it is in now.</summary>
    Task OnEventStreamsChangedAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>
/// Why a connection was closed, independent of the transport. Every reason means the same to a
/// client — reconnect, logging in first for <see cref="SessionEnded"/>, and re-read the current state —
/// except <see cref="ProtocolViolation"/>, which is a bug in the client.
/// </summary>
public enum EventCloseReason
{
    /// <summary>The client closed it, or it simply finished.</summary>
    Normal,

    /// <summary>The session behind it was logged out, revoked by a password change, or lapsed.</summary>
    SessionEnded,

    /// <summary>It fell further behind than <see cref="EventOptions.MaximumPendingEvents"/>.</summary>
    TooSlow,

    /// <summary>The server is shutting down.</summary>
    ServerStopping,

    /// <summary>The client sent something the stream does not accept.</summary>
    ProtocolViolation,

    /// <summary>
    /// The network connection went away underneath it, or the client stopped sending heartbeats for
    /// longer than <see cref="EventOptions.HeartbeatTimeout"/>.
    /// </summary>
    ConnectionLost
}
