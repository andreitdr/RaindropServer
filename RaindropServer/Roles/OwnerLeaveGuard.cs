using RaindropServer.Users;

namespace RaindropServer.Roles;

/// <summary>
/// Refuses to let the only owner erase their account. An erased account can never log in again, so
/// the owner role would stay bound to a UUID nobody can use and the server could only be recovered
/// from its console.
/// </summary>
/// <remarks>
/// Leaving with the data preserved is allowed: the owner keeps the role and can rejoin — by UUID or
/// by username — until the retention window closes.
/// </remarks>
public sealed class OwnerLeaveGuard : ILeaveGuard
{
    private readonly RoleService _roles;

    public OwnerLeaveGuard(RoleService roles) => _roles = roles;

    /// <inheritdoc />
    public async Task CheckLeaveAsync(UserRecord user, bool preserveData, CancellationToken cancellationToken)
    {
        if (preserveData)
            return;

        if (await _roles.IsSoleOwnerAsync(user.UserId, cancellationToken))
            throw ApiException.Conflict("last_owner",
                "You are the only member with the owner role. Give it to somebody else first, or leave " +
                "with your data preserved so you can rejoin.");
    }
}
