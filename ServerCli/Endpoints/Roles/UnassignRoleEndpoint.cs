using RaindropServer;
using RaindropServer.Roles;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Roles;

/// <summary>
/// POST /api/roles/unassign — takes a role away from a member. The last member holding the owner
/// role keeps it: there is no account above them to put a server with no owner right again.
/// </summary>
public sealed class UnassignRoleEndpoint : IAuthenticatedTypedEndpoint<MemberRoleRequest, MemberRoleChangedResponse>
{
    private readonly RoleService _roles;

    public UnassignRoleEndpoint(RoleService roles) => _roles = roles;

    public string Route => "/api/roles/unassign";
    public HttpMethod Method => HttpMethod.Post;

    public Task<MemberRoleChangedResponse> HandleAsync(
        UserSession session, MemberRoleRequest request, CancellationToken cancellationToken) =>
        _roles.UnassignAsync(session, request, cancellationToken);
}
