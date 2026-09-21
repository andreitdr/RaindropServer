namespace RaindropServer.Members;

/// <summary>
/// Answers where each member sits in <see cref="PresenceStatus"/> right now.
/// </summary>
/// <remarks>
/// A seam rather than a hard-coded rule: the default reads live sessions
/// (<see cref="SessionPresenceTracker"/>), but a server sharing sessions across several instances,
/// or one that lets members set their own status, replaces this and nothing else changes. Statuses
/// are asked for in a batch so that a roster costs one call however many members it has, and the
/// call is asynchronous so an implementation that has to go somewhere for the answer can.
/// </remarks>
public interface IPresenceTracker
{
    /// <summary>
    /// The current status of each of <paramref name="userIds"/>. Every id asked about appears in the
    /// result — a member nothing is known about is <see cref="PresenceStatus.Offline"/>, not absent.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, PresenceStatus>> GetAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);
}
