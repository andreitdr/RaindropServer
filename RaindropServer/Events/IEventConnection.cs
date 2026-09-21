using RaindropServer.Payloads;
using RaindropServer.Users;

namespace RaindropServer.Events;

/// <summary>
/// One client listening for events, whatever carries them. The WebSocket stream is the transport this
/// server ships (<c>Events/WebSockets/</c>); another — server-sent events, a push gateway — is added by
/// implementing this and registering its connections with <see cref="IEventConnectionRegistry"/>, and
/// nothing that publishes has to change.
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

    /// <summary>The wire format the client asked for. Frames arrive already encoded in it.</summary>
    IPayloadCodec Codec { get; }

    /// <summary>Queues one encoded <see cref="ServerEvent{T}"/> for the client. Must not block.</summary>
    void Deliver(byte[] frame);

    /// <summary>
    /// Asks for the connection to be closed. Only the first request counts, so the reason a client is
    /// given is the reason its connection actually ended. Must not block.
    /// </summary>
    void Close(EventCloseReason reason);
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

    /// <summary>The network connection went away underneath it.</summary>
    ConnectionLost
}
