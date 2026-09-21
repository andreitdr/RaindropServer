using RaindropServer;
using RaindropServer.Roles;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Roles;

/// <summary>
/// POST /api/roles/delete — deletes a role and takes it away from everyone holding it. The two
/// built-in roles are refused: a server whose owner role could be deleted is one nobody can
/// administer again.
/// </summary>
public sealed class DeleteRoleEndpoint : IAuthenticatedTypedEndpoint<RoleRequest, RoleDeletedResponse>
{
    private readonly RoleService _roles;

    public DeleteRoleEndpoint(RoleService roles) => _roles = roles;

    public string Route => "/api/roles/delete";
    public HttpMethod Method => HttpMethod.Post;

    public Task<RoleDeletedResponse> HandleAsync(
        UserSession session, RoleRequest request, CancellationToken cancellationToken) =>
        _roles.DeleteAsync(session, request, cancellationToken);
}
