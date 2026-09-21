using RaindropServer;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Users;

/// <summary>
/// POST /api/users/logout — ends this session. Clients call it when they close, which is what keeps
/// the login state from outliving the application; sessions that are never logged out simply lapse.
/// </summary>
public sealed class LogoutEndpoint : IAuthenticatedEndpoint
{
    private readonly UserService _users;

    public LogoutEndpoint(UserService users) => _users = users;

    public string Route => "/api/users/logout";
    public HttpMethod Method => HttpMethod.Post;

    public Task HandleAsync(UserSession session, HttpContext context, CancellationToken cancellationToken) =>
        EndpointResponse.WriteAsync(context, cancellationToken, ct => _users.LogoutAsync(session, ct));
}
