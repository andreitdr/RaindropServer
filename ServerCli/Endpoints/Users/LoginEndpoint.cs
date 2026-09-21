using RaindropServer;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Users;

/// <summary>
/// POST /api/users/login — exchanges a UUID (or username) and password for a session token. The
/// session lives only in the server's memory and lapses when the client stops calling.
/// </summary>
public sealed class LoginEndpoint : ITypedEndpoint<LoginRequest, SessionResponse>
{
    private readonly UserService _users;

    public LoginEndpoint(UserService users) => _users = users;

    public string Route => "/api/users/login";
    public HttpMethod Method => HttpMethod.Post;

    public Task<SessionResponse> HandleAsync(LoginRequest request, CancellationToken cancellationToken) =>
        _users.LoginAsync(request, cancellationToken);
}
