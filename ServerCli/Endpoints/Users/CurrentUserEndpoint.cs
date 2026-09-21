using RaindropServer;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Users;

/// <summary>GET /api/users/me — the caller's own account on this server.</summary>
public sealed class CurrentUserEndpoint : IAuthenticatedEndpoint
{
    private readonly UserService _users;

    public CurrentUserEndpoint(UserService users) => _users = users;

    public string Route => "/api/users/me";
    public HttpMethod Method => HttpMethod.Get;

    public Task HandleAsync(UserSession session, HttpContext context, CancellationToken cancellationToken) =>
        EndpointResponse.WriteAsync(context, cancellationToken, ct => _users.GetProfileAsync(session, ct));
}
