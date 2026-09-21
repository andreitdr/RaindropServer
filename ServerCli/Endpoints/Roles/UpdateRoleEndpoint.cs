using RaindropServer;
using RaindropServer.Roles;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Roles;

/// <summary>
/// POST /api/roles/update — renames a role, replaces its permissions, or moves it in the list. Every
/// field but the id is optional and left alone when absent, so renaming a role cannot accidentally
/// clear what it grants.
/// </summary>
public sealed class UpdateRoleEndpoint : IAuthenticatedTypedEndpoint<UpdateRoleRequest, RoleSummary>
{
    private readonly RoleService _roles;

    public UpdateRoleEndpoint(RoleService roles) => _roles = roles;

    public string Route => "/api/roles/update";
    public HttpMethod Method => HttpMethod.Post;

    public Task<RoleSummary> HandleAsync(
        UserSession session, UpdateRoleRequest request, CancellationToken cancellationToken) =>
        _roles.UpdateAsync(session, request, cancellationToken);
}
