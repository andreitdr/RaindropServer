using RaindropServer;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Users;

/// <summary>
/// POST /api/users/me/leave — leaves the server for good, either keeping the account for a possible
/// return or erasing it now. Confirmed with the account password because it cannot be undone.
/// </summary>
public sealed class LeaveServerEndpoint : IAuthenticatedTypedEndpoint<LeaveRequest, LeaveResponse>
{
    private readonly UserService _users;

    public LeaveServerEndpoint(UserService users) => _users = users;

    public string Route => "/api/users/me/leave";
    public HttpMethod Method => HttpMethod.Post;

    public Task<LeaveResponse> HandleAsync(
        UserSession session, LeaveRequest request, CancellationToken cancellationToken) =>
        _users.LeaveAsync(session, request, cancellationToken);
}
