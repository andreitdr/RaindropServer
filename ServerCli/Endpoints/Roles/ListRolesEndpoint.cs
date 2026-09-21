using RaindropServer;
using RaindropServer.Roles;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Roles;

/// <summary>
/// GET /api/roles — every role on this server, highest position first. Readable by any member: a
/// client needs the list to show who is a moderator, and a role list is not a secret.
/// </summary>
public sealed class ListRolesEndpoint : IAuthenticatedEndpoint
{
    private readonly RoleService _roles;

    public ListRolesEndpoint(RoleService roles) => _roles = roles;

    public string Route => "/api/roles";
    public HttpMethod Method => HttpMethod.Get;

    public Task HandleAsync(UserSession session, HttpContext context, CancellationToken cancellationToken) =>
        EndpointResponse.WriteAsync(context, cancellationToken, ct => _roles.ListAsync(session, ct));
}
