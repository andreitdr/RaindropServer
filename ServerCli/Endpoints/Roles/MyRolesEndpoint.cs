using RaindropServer;
using RaindropServer.Roles;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Roles;

/// <summary>
/// GET /api/roles/me — the caller's own roles and what they add up to across the server. What a
/// client reads once after logging in to decide which administrative screens to offer at all;
/// per-channel decisions still come from each channel's <c>yourPermissions</c>.
/// </summary>
public sealed class MyRolesEndpoint : IAuthenticatedEndpoint
{
    private readonly RoleService _roles;

    public MyRolesEndpoint(RoleService roles) => _roles = roles;

    public string Route => "/api/roles/me";
    public HttpMethod Method => HttpMethod.Get;

    public Task HandleAsync(UserSession session, HttpContext context, CancellationToken cancellationToken) =>
        EndpointResponse.WriteAsync(context, cancellationToken,
            ct => _roles.GetMemberRolesAsync(session, session.UserId, ct));
}
