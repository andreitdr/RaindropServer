using RaindropServer;
using RaindropServer.Roles;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Roles;

/// <summary>
/// POST /api/roles/get — one role and the UUIDs of the members holding it. A POST rather than a GET
/// with the id in the path, for the reason every other lookup here is: nothing identifying goes in a
/// URL, which tends to end up in logs.
/// </summary>
public sealed class GetRoleEndpoint : IAuthenticatedTypedEndpoint<RoleRequest, RoleDetail>
{
    private readonly RoleService _roles;

    public GetRoleEndpoint(RoleService roles) => _roles = roles;

    public string Route => "/api/roles/get";
    public HttpMethod Method => HttpMethod.Post;

    public Task<RoleDetail> HandleAsync(
        UserSession session, RoleRequest request, CancellationToken cancellationToken) =>
        _roles.GetAsync(session, request, cancellationToken);
}
