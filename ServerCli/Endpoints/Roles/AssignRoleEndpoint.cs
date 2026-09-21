using RaindropServer;
using RaindropServer.Roles;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Roles;

/// <summary>
/// POST /api/roles/assign — gives a member a role. Needs <c>manage_roles</c> and everything the role
/// grants, which is what makes handing out the owner role something only an owner can do.
/// </summary>
public sealed class AssignRoleEndpoint : IAuthenticatedTypedEndpoint<MemberRoleRequest, MemberRoleChangedResponse>
{
    private readonly RoleService _roles;

    public AssignRoleEndpoint(RoleService roles) => _roles = roles;

    public string Route => "/api/roles/assign";
    public HttpMethod Method => HttpMethod.Post;

    public Task<MemberRoleChangedResponse> HandleAsync(
        UserSession session, MemberRoleRequest request, CancellationToken cancellationToken) =>
        _roles.AssignAsync(session, request, cancellationToken);
}
