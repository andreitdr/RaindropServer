using RaindropServer;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Users;

/// <summary>
/// POST /api/users/register — first login. The client sends the UUID it generated for this server
/// along with the username and password it wants, and gets a session straight back.
/// </summary>
public sealed class RegisterEndpoint : ITypedEndpoint<RegisterRequest, SessionResponse>
{
    private readonly UserService _users;

    public RegisterEndpoint(UserService users) => _users = users;

    public string Route => "/api/users/register";
    public HttpMethod Method => HttpMethod.Post;

    public Task<SessionResponse> HandleAsync(RegisterRequest request, CancellationToken cancellationToken) =>
        _users.RegisterAsync(request, cancellationToken);
}
