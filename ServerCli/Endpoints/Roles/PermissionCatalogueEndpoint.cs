using RaindropServer;
using RaindropServer.Roles;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Roles;

/// <summary>
/// GET /api/roles/permissions — every permission this server understands, in the order a permission
/// editor should list them. A client builds its role editor from this rather than from a hard-coded
/// list, so a server running a newer build offers the permissions it actually has.
/// </summary>
public sealed class PermissionCatalogueEndpoint : IAuthenticatedEndpoint
{
    public string Route => "/api/roles/permissions";
    public HttpMethod Method => HttpMethod.Get;

    public Task HandleAsync(UserSession session, HttpContext context, CancellationToken cancellationToken) =>
        EndpointResponse.WriteAsync(context, cancellationToken, _ => Task.FromResult(RoleService.Catalogue()));
}
