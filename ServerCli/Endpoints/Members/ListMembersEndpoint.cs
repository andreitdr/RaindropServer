using RaindropServer;
using RaindropServer.Members;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Members;

/// <summary>
/// GET /api/members — every active member of this server and whether each one is online, ordered by
/// presence and then by username. This is what a client draws its member list from.
/// </summary>
public sealed class ListMembersEndpoint : IAuthenticatedEndpoint
{
    private readonly MemberService _members;

    public ListMembersEndpoint(MemberService members) => _members = members;

    public string Route => "/api/members";
    public HttpMethod Method => HttpMethod.Get;

    public Task HandleAsync(UserSession session, HttpContext context, CancellationToken cancellationToken) =>
        EndpointResponse.WriteAsync(context, cancellationToken, ct => _members.ListAsync(session, ct));
}
