using RaindropServer;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Users;

/// <summary>
/// POST /api/users/me/password — changes the password after confirming the current one. Every other
/// session for this user is dropped, and the response carries a fresh token for the caller.
/// </summary>
public sealed class ChangePasswordEndpoint : IAuthenticatedTypedEndpoint<ChangePasswordRequest, SessionResponse>
{
    private readonly UserService _users;

    public ChangePasswordEndpoint(UserService users) => _users = users;

    public string Route => "/api/users/me/password";
    public HttpMethod Method => HttpMethod.Post;

    public Task<SessionResponse> HandleAsync(
        UserSession session, ChangePasswordRequest request, CancellationToken cancellationToken) =>
        _users.ChangePasswordAsync(session, request, cancellationToken);
}
