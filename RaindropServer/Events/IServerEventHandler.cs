namespace RaindropServer.Events;

/// <summary>
/// Server code that reacts to events — the in-process counterpart of a connected client, and the
/// hook a plugin uses to act when something happens (a bot answering a message, an audit log
/// recording a role change). Register one in the container and it receives every event whose payload
/// is <typeparamref name="T"/>; check <see cref="ServerEvent{T}.Type"/> to tell, say,
/// <c>message.created</c> from <c>message.deleted</c>, which share <c>ChannelMessage</c>.
/// </summary>
/// <remarks>
/// <para>
/// Handlers are not subject to the event's audience: they are the server, not a member being shown
/// something. That also means a handler that repeats what it hears to members has to apply the rules
/// about who may see it itself.
/// </para>
/// <para>
/// Handlers run inside the publish, after the event has been queued for clients, so the request that
/// caused it waits for them. Keep them quick, and hand anything slow to a background queue of your
/// own. A handler that throws is reported and stepped over.
/// </para>
/// </remarks>
public interface IServerEventHandler<T>
{
    Task HandleAsync(ServerEvent<T> serverEvent, CancellationToken cancellationToken);
}
