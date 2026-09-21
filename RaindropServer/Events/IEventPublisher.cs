namespace RaindropServer.Events;

/// <summary>
/// The one way to announce that something changed on this server. Core services publish through it
/// after a change is written, and so can a plugin: event types are open-ended names, so a plugin
/// announces its own by picking a name nothing else uses — prefixing it with the plugin's own name is
/// the convention (<c>polls.vote_cast</c>).
/// </summary>
/// <remarks>
/// <para>
/// An event goes two ways. <b>Connected clients</b> in the audience receive it over whatever
/// <see cref="IEventConnection"/> transport they opened. <b>In-process handlers</b> — every
/// <see cref="IServerEventHandler{T}"/> registered for the payload type — receive it whatever the
/// audience, because they are server code rather than a member being shown something.
/// </para>
/// <para>
/// Publishing never fails the caller: the change is already written by the time it is announced, so
/// a broadcast or handler that goes wrong is reported and stepped over. Nor does it wait on a slow
/// client — each connection buffers or closes on its own.
/// </para>
/// <para>
/// Events are notifications, not a source of truth. Every payload should carry the full current state
/// of what it describes, so a client can merge it by id exactly as it would a page it fetched, and a
/// client that missed events while disconnected catches up by re-reading after
/// <see cref="ServerEventTypes.Ready"/>. Nothing is replayed.
/// </para>
/// </remarks>
public interface IEventPublisher
{
    /// <summary>
    /// Sends the same payload to every member in <paramref name="audience"/> — every connected member
    /// when it is null — and to every handler for <typeparamref name="T"/>.
    /// </summary>
    Task PublishAsync<T>(string type, T data, EventAudience? audience = null);

    /// <summary>
    /// Sends a payload that differs from one member to the next — a channel carries the recipient's
    /// own permissions, for instance. <paramref name="project"/> is asked once per connected member,
    /// and answering null leaves that member out. Handlers receive <paramref name="data"/>, the
    /// version that belongs to nobody in particular.
    /// </summary>
    Task PublishPerRecipientAsync<T>(string type, T data, EventProjection<T> project) where T : class;
}

/// <summary>
/// Decides whether one member should receive an event — typically "may they see this channel".
/// Throwing counts as no: being unable to tell whether somebody may see a thing means they may not.
/// </summary>
public delegate ValueTask<bool> EventAudience(Guid userId, CancellationToken cancellationToken);

/// <summary>Builds one member's copy of an event, or answers null to leave them out.</summary>
public delegate Task<T?> EventProjection<T>(Guid userId, CancellationToken cancellationToken) where T : class;

/// <summary>Audiences that do not depend on any one subsystem's rules.</summary>
public static class EventAudiences
{
    /// <summary>Every connected member. The same as passing no audience at all.</summary>
    public static EventAudience Everyone { get; } = (_, _) => ValueTask.FromResult(true);

    /// <summary>Only the given members — the member an event is about, say.</summary>
    public static EventAudience Only(params Guid[] userIds)
    {
        var set = userIds.ToHashSet();
        return (userId, _) => ValueTask.FromResult(set.Contains(userId));
    }
}
