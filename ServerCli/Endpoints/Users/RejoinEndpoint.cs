using RaindropServer;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Users;

/// <summary>
/// POST /api/users/rejoin — brings back an account that left with its data preserved, using the
/// password it had at the time. Only works while the retention window is still open.
/// </summary>
public sealed class RejoinEndpoint : ITypedEndpoint<RejoinRequest, SessionResponse>
{
    private readonly UserService _users;

    public RejoinEndpoint(UserService users) => _users = users;

    public string Route => "/api/users/rejoin";
    public HttpMethod Method => HttpMethod.Post;

    public Task<SessionResponse> HandleAsync(RejoinRequest request, CancellationToken cancellationToken) =>
        _users.RejoinAsync(request, cancellationToken);
}
