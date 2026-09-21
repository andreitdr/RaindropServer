using RaindropServer;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Users;

/// <summary>
/// POST /api/users/username-available — lets the registration screen tell the user their chosen name
/// is taken before they fill in a password. Usernames are unique to this server only.
/// </summary>
public sealed class UsernameAvailabilityEndpoint
    : ITypedEndpoint<UsernameAvailabilityRequest, UsernameAvailabilityResponse>
{
    private readonly UserService _users;

    public UsernameAvailabilityEndpoint(UserService users) => _users = users;

    public string Route => "/api/users/username-available";
    public HttpMethod Method => HttpMethod.Post;

    public Task<UsernameAvailabilityResponse> HandleAsync(
        UsernameAvailabilityRequest request, CancellationToken cancellationToken) =>
        _users.CheckUsernameAsync(request.Username, cancellationToken);
}
