using RaindropServer;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Users;

/// <summary>
/// POST /api/users/me/username — claims a different username on this server, subject to the same
/// uniqueness rule as registration.
/// </summary>
public sealed class ChangeUsernameEndpoint : IAuthenticatedTypedEndpoint<ChangeUsernameRequest, UserProfile>
{
    private readonly UserService _users;

    public ChangeUsernameEndpoint(UserService users) => _users = users;

    public string Route => "/api/users/me/username";
    public HttpMethod Method => HttpMethod.Post;

    public Task<UserProfile> HandleAsync(
        UserSession session, ChangeUsernameRequest request, CancellationToken cancellationToken) =>
        _users.ChangeUsernameAsync(session, request, cancellationToken);
}
