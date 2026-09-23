namespace RaindropServer.Users;

/// <summary>
/// Something that may refuse to let a member leave the server. Registered in the container and asked
/// by <see cref="UserService"/> once the leave has been confirmed with the password, before anything
/// is written.
/// </summary>
/// <remarks>
/// The same seam as <see cref="IRegistrationListener"/>, so that <c>Users/</c> does not have to know
/// which subsystems care: it is how the roles subsystem stops the only owner erasing their account
/// and leaving a server nobody can administer. Unlike a listener, a guard that throws <b>stops the
/// leave</b> — throw an <see cref="ApiException"/> to tell the client why.
/// </remarks>
public interface ILeaveGuard
{
    /// <summary>Throws to refuse <paramref name="user"/> leaving; returns to let them.</summary>
    /// <param name="user">The active member asking to leave.</param>
    /// <param name="preserveData">Whether they asked to keep their account for a possible return.</param>
    Task CheckLeaveAsync(UserRecord user, bool preserveData, CancellationToken cancellationToken);
}
