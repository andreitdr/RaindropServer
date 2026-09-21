using RaindropServer;
using RaindropServer.Roles;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Roles;

/// <summary>
/// POST /api/roles/create — creates a role. Needs <c>manage_roles</c>, and every permission the new
/// role would grant: managing roles lets you delegate your own authority, not exceed it.
/// </summary>
public sealed class CreateRoleEndpoint : IAuthenticatedTypedEndpoint<CreateRoleRequest, RoleSummary>
{
    private readonly RoleService _roles;

    public CreateRoleEndpoint(RoleService roles) => _roles = roles;

    public string Route => "/api/roles/create";
    public HttpMethod Method => HttpMethod.Post;

    public Task<RoleSummary> HandleAsync(
        UserSession session, CreateRoleRequest request, CancellationToken cancellationToken) =>
        _roles.CreateAsync(session, request, cancellationToken);
}
