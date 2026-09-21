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
    /// How often the server pings an idle connection, so that proxies and NAT boxes do not drop it
    /// and a client that vanished is noticed.
    /// </summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often an open connection renews its session. An open stream is a client that is still
    /// there, so its session should not lapse underneath it just because it has had no reason to make
    /// a request. Capped at half the session idle timeout, whatever this says.
    /// </summary>
    public TimeSpan SessionRenewalInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a closing handshake may take before the connection is simply dropped.</summary>
    public TimeSpan CloseTimeout { get; set; } = TimeSpan.FromSeconds(2);
}
