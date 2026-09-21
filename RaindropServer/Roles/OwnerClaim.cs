using RaindropServer.Users;

namespace RaindropServer.Roles;

/// <summary>
/// Hands the owner role to the first UUID that registers, which is how a freshly installed server
/// ends up with somebody able to administer it. A self-hosted Raindrop server has no account handed
/// down from anywhere, so without this the first member would have only what the everyone role
/// allows and nobody could ever create a role.
/// </summary>
/// <remarks>
/// It claims nothing once the role is held, so the second member to register is an ordinary one, and
/// nothing here can take ownership away from somebody who has it. A server owner who would rather
/// grant the role from the console turns
/// <see cref="RoleOptions.FirstMemberBecomesOwner"/> off.
/// </remarks>
public sealed class OwnerClaim : IRegistrationListener
{
    // Two clients registering at the same instant on an empty server would otherwise both find no
    // owner and both become one. A single-process server needs nothing more than this to serialise it.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly RoleService _roles;

    public OwnerClaim(RoleService roles) => _roles = roles;

    /// <inheritdoc />
    public async Task OnRegisteredAsync(UserRecord user, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (await _roles.TryClaimOwnershipAsync(user.UserId, cancellationToken))
                Console.WriteLine($"[raindrop] {user.Username} is the first member here and now has the owner role.");
        }
        finally
        {
            _gate.Release();
        }
    }
}
