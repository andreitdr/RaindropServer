namespace RaindropServer.Users;

/// <summary>
/// Something that needs to know when a UUID becomes a member for the first time. Registered in the
/// container and called by <see cref="UserService"/> after the account row is written.
/// </summary>
/// <remarks>
/// This exists so that <c>Users/</c> does not have to know about the subsystems that care about a
/// new member: it is how the roles subsystem hands the first UUID to register the owner role,
/// without registration itself learning what a role is. A listener that throws does not fail the
/// registration — the account is already written, and refusing the session would leave the client
/// unable to log in to a server it is now a member of.
/// </remarks>
public interface IRegistrationListener
{
    /// <summary>Called once, just after <paramref name="user"/> first becomes an active member.</summary>
    Task OnRegisteredAsync(UserRecord user, CancellationToken cancellationToken);
}
