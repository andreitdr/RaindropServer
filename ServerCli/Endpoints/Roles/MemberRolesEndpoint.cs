using RaindropServer;
using RaindropServer.Roles;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Roles;

/// <summary>
/// POST /api/roles/member — which roles one member holds, and what they add up to. Any member may
/// ask about any other: this is how a client shows "Moderator" next to somebody in the roster.
/// </summary>
public sealed class MemberRolesEndpoint : IAuthenticatedTypedEndpoint<MemberRolesRequest, MemberRolesResponse>
{
    private readonly RoleService _roles;

    public MemberRolesEndpoint(RoleService roles) => _roles = roles;

    public string Route => "/api/roles/member";
    public HttpMethod Method => HttpMethod.Post;

    public Task<MemberRolesResponse> HandleAsync(
        UserSession session, MemberRolesRequest request, CancellationToken cancellationToken) =>
        _roles.GetMemberRolesAsync(session, request.UserId, cancellationToken);
}
