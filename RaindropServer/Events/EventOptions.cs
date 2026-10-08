namespace RaindropServer.Events;

/// <summary>How the event stream treats the connections it holds.</summary>
public sealed class EventOptions
{
    /// <summary>
    /// How many events may wait to be sent to one connection before it is judged too slow and closed
    /// as <see cref="EventCloseReason.TooSlow"/>. A client that falls this far behind is better
    /// off reconnecting and re-reading the current state than receiving a backlog of stale events —
    /// and the server is better off not buffering without limit on its behalf.
    /// </summary>
    public int MaximumPendingEvents { get; set; } = 256;

    /// <summary>
    /// How often a client sends a heartbeat on its stream, as told in <see cref="ReadyEvent"/>. Short
    /// enough that proxies and NAT boxes keep an idle stream open.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long a stream may go without a heartbeat before the client is taken to be gone and the
    /// stream is closed as <see cref="EventCloseReason.ConnectionLost"/>. This is what makes presence
    /// honest: a client that was killed rather than closed reads as offline this long afterwards, not
    /// when its session lapses. Several intervals, so one late heartbeat is not a disconnection.
    /// </summary>
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How often an open connection renews its session. An open stream is a client that is still
    /// there, so its session should not lapse underneath it just because it has had no reason to make
    /// a request. Capped at half the session idle timeout, whatever this says.
    /// </summary>
    public TimeSpan SessionRenewalInterval { get; set; } = TimeSpan.FromMinutes(5);
}
