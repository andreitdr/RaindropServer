using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RaindropServer.Users;

namespace RaindropServer.Events;

/// <summary>
/// The default <see cref="IEventPublisher"/> and <see cref="IEventConnectionRegistry"/>: every open
/// connection in one dictionary, in this process.
/// </summary>
/// <remarks>
/// <para>
/// That is correct because a Raindrop server is a single process — the same assumption
/// <see cref="InMemorySessionStore"/> and the permission cache make. A server spread over several
/// instances would replace this with a publisher that relays through something shared; everything
/// that publishes depends only on the interfaces, so nothing else would change.
/// </para>
/// <para>
/// Besides fanning events out, the registry owns what every transport would otherwise have to get
/// right for itself: <see cref="ServerEventTypes.Ready"/> first, closing a connection whose session
/// ends, keeping an open connection's session alive, telling <see cref="IEventStreamListener"/>s when a
/// member starts or stops listening, and closing everything when the server stops.
/// </para>
/// </remarks>
public sealed class EventHub : IEventPublisher, IEventConnectionRegistry, ISessionListener, IDisposable
{
    private readonly ConcurrentDictionary<Guid, IEventConnection> _connections = new();
    private readonly ISessionStore _sessions;
    private readonly ServerIdentity _identity;
    private readonly EventOptions _options;
    private readonly IServiceProvider _services;
    private readonly Timer _renewal;
    private readonly CancellationTokenRegistration _stopping;
    private volatile bool _stopped;

    public EventHub(
        ISessionStore sessions,
        ServerIdentity identity,
        EventOptions options,
        IServiceProvider services,
        UserOptions? users = null,
        IHostApplicationLifetime? lifetime = null)
    {
        _sessions = sessions;
        _identity = identity;
        _options = options;
        _services = services;

        // Renewing any less often than this would let an open connection's session lapse between two
        // renewals.
        var half = (users ?? new UserOptions()).SessionIdleTimeout / 2;
        var interval = options.SessionRenewalInterval < half ? options.SessionRenewalInterval : half;

        if (interval <= TimeSpan.Zero)
            interval = TimeSpan.FromSeconds(1);

        _renewal = new Timer(_ => RenewSessions(), null, interval, interval);

        // A server shutting down says so, rather than leaving every client to find out from a dropped
        // connection — and it has to say so now, while there is still time for a close handshake.
        if (lifetime is not null)
            _stopping = lifetime.ApplicationStopping.Register(CloseAll);
    }

    // ── IEventConnectionRegistry ─────────────────────────────────────────────

    /// <inheritdoc />
    public int Count => _connections.Count;

    /// <inheritdoc />
    public IDisposable Register(IEventConnection connection)
    {
        // Delivered before the connection is visible to publishers, so ready is always the first frame.
        DeliverReady(connection);

        _connections[connection.Id] = connection;

        // Checked after adding, so a stop or a revocation that raced the registration is not missed.
        if (_stopped)
            connection.Close(EventCloseReason.ServerStopping);
        else if (!_sessions.IsActive(connection.Session.Token))
            connection.Close(EventCloseReason.SessionEnded);

        if (CountFor(connection.Session.UserId) == 1)
            NotifyStreamsChanged(connection.Session.UserId);

        return new Registration(this, connection);
    }

    /// <inheritdoc />
    public void Resubscribe(IEventConnection connection)
    {
        if (_connections.ContainsKey(connection.Id))
            DeliverReady(connection);
    }

    /// <inheritdoc />
    public IReadOnlySet<Guid> ConnectedUserIds() =>
        _connections.Values.Select(connection => connection.Session.UserId).ToHashSet();

    // ── ISessionListener ─────────────────────────────────────────────────────

    /// <summary>
    /// Closes every connection whose session has just ended — logged out, revoked by a password
    /// change, or taken away by leaving. Without this a revoked token would keep listening.
    /// </summary>
    public Task OnSessionsChangedAsync(Guid userId, CancellationToken cancellationToken)
    {
        foreach (var connection in _connections.Values)
        {
            if (connection.Session.UserId == userId && !_sessions.IsActive(connection.Session.Token))
                connection.Close(EventCloseReason.SessionEnded);
        }

        return Task.CompletedTask;
    }

    // ── IEventPublisher ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task PublishAsync<T>(string type, T data, EventAudience? audience = null)
    {
        var serverEvent = new ServerEvent<T>(type, data);

        try
        {
            // Encoded once per transport, however many connections hear it.
            var frames = new Dictionary<IEventEncoder, object?>();

            foreach (var (userId, connections) in Recipients())
            {
                if (audience is null || await IncludesAsync(audience, userId))
                    Deliver(connections, serverEvent, frames);
            }
        }
        catch (Exception ex)
        {
            Report(type, ex);
        }

        await HandleAsync(serverEvent);
    }

    /// <inheritdoc />
    public async Task PublishPerRecipientAsync<T>(string type, T data, EventProjection<T> project) where T : class
    {
        try
        {
            foreach (var (userId, connections) in Recipients())
            {
                T? own;

                try
                {
                    // Not the publishing request's token: the change is committed, and a caller hanging
                    // up straight afterwards must not stop everyone else from hearing about it.
                    own = await project(userId, CancellationToken.None);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // One member the answer could not be worked out for — refused access, say — is no
                    // reason to keep the event from everybody else.
                    continue;
                }

                if (own is not null)
                    Deliver(connections, new ServerEvent<T>(type, own), new Dictionary<IEventEncoder, object?>());
            }
        }
        catch (Exception ex)
        {
            Report(type, ex);
        }

        await HandleAsync(new ServerEvent<T>(type, data));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The open connections grouped by member, so an audience is asked about each member once however
    /// many devices they have connected. A connection whose session has ended is closed here rather
    /// than sent anything — the last line of defence if a revocation was never announced.
    /// </summary>
    private List<(Guid UserId, List<IEventConnection> Connections)> Recipients()
    {
        var byUser = new Dictionary<Guid, List<IEventConnection>>();

        foreach (var connection in _connections.Values)
        {
            if (!_sessions.IsActive(connection.Session.Token))
            {
                connection.Close(EventCloseReason.SessionEnded);
                continue;
            }

            if (!byUser.TryGetValue(connection.Session.UserId, out var list))
                byUser[connection.Session.UserId] = list = [];

            list.Add(connection);
        }

        return byUser.Select(entry => (entry.Key, entry.Value)).ToList();
    }

    private static void Deliver<T>(
        List<IEventConnection> connections, ServerEvent<T> serverEvent, Dictionary<IEventEncoder, object?> frames)
    {
        foreach (var connection in connections)
        {
            if (!frames.TryGetValue(connection.Encoder, out var frame))
                frames[connection.Encoder] = frame = connection.Encoder.Encode(serverEvent);

            // A transport that cannot carry this event — a plugin's, with no encoding for its payload —
            // simply does not hear it.
            if (frame is not null)
                connection.Deliver(frame);
        }
    }

    private void DeliverReady(IEventConnection connection)
    {
        var ready = connection.Encoder.Encode(new ServerEvent<ReadyEvent>(ServerEventTypes.Ready,
            new ReadyEvent(connection.Session.UserId, _identity.ProtocolVersion, _options.HeartbeatInterval)));

        if (ready is not null)
            connection.Deliver(ready);
    }

    private int CountFor(Guid userId) =>
        _connections.Values.Count(connection => connection.Session.UserId == userId);

    /// <summary>
    /// Tells every <see cref="IEventStreamListener"/> that a member started or stopped listening. Not
    /// awaited: it runs from inside a registration or a disposal, neither of which may wait on whatever
    /// a listener does (publishing presence, for one).
    /// </summary>
    private void NotifyStreamsChanged(Guid userId) => _ = Task.Run(async () =>
    {
        IEnumerable<IEventStreamListener> listeners;

        try
        {
            // Resolved late: presence listens here and publishes through this hub, so taking the
            // listeners in the constructor would be a cycle.
            listeners = _services.GetServices<IEventStreamListener>();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[raindrop] Could not find event stream listeners: {ex.Message}");
            return;
        }

        foreach (var listener in listeners)
        {
            try
            {
                await listener.OnEventStreamsChangedAsync(userId, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"[raindrop] {listener.GetType().Name} failed: {ex.Message}");
            }
        }
    });

    private static async ValueTask<bool> IncludesAsync(EventAudience audience, Guid userId)
    {
        try
        {
            return await audience(userId, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Unable to tell whether they may see it means they may not.
            return false;
        }
    }

    /// <summary>Runs every in-process handler for the payload type, stepping over any that fail.</summary>
    private async Task HandleAsync<T>(ServerEvent<T> serverEvent)
    {
        IEnumerable<IServerEventHandler<T>> handlers;

        try
        {
            handlers = _services.GetServices<IServerEventHandler<T>>();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Report(serverEvent.Type, ex);
            return;
        }

        foreach (var handler in handlers)
        {
            try
            {
                await handler.HandleAsync(serverEvent, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine(
                    $"[raindrop] {handler.GetType().Name} failed handling '{serverEvent.Type}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// An open connection is a client that is plainly still here even if it has had nothing to ask,
    /// so its session should not lapse underneath it. A session that has ended anyway closes it.
    /// </summary>
    private void RenewSessions()
    {
        foreach (var connection in _connections.Values)
        {
            if (!_sessions.TryValidate(connection.Session.Token, out _))
                connection.Close(EventCloseReason.SessionEnded);
        }
    }

    private void CloseAll()
    {
        _stopped = true;

        foreach (var connection in _connections.Values)
            connection.Close(EventCloseReason.ServerStopping);
    }

    private static void Report(string type, Exception ex) =>
        Console.Error.WriteLine($"[raindrop] Publishing '{type}' failed: {ex.Message}");

    public void Dispose()
    {
        _renewal.Dispose();
        _stopping.Dispose();
    }

    private sealed class Registration(EventHub hub, IEventConnection connection) : IDisposable
    {
        public void Dispose()
        {
            if (hub._connections.TryRemove(connection.Id, out _) && hub.CountFor(connection.Session.UserId) == 0)
                hub.NotifyStreamsChanged(connection.Session.UserId);
        }
    }
}
