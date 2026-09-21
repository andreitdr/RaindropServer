namespace RaindropServer.Users;

/// <summary>
/// Something that needs to know when a member's sessions change — one opened, one ended, or all of
/// them revoked at once. Registered in the container and called by <see cref="UserService"/> after
/// the change has been made.
/// </summary>
/// <remarks>
/// The same seam as <see cref="IRegistrationListener"/>, for the same reason: <c>Users/</c> should
/// not have to know who cares. Presence is derived from sessions, so this is how the roster learns a
/// member came online; and the event stream is bound to a session, so this is how it learns to close
/// a connection whose token has just been revoked. A listener that throws is reported and stepped
/// over — the session change has already happened.
/// </remarks>
public interface ISessionListener
{
    /// <summary>Called after the set of sessions held by <paramref name="userId"/> has changed.</summary>
    Task OnSessionsChangedAsync(Guid userId, CancellationToken cancellationToken);
}
