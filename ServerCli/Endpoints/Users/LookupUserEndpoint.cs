using RaindropServer;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Users;

/// <summary>
/// POST /api/users/lookup — resolves a UUID to whatever another member may see. Messages are linked
/// to UUIDs, so this is how a client puts a name next to one; for an account that has been erased
/// the name comes back null and the client shows a deleted user.
/// </summary>
public sealed class LookupUserEndpoint : IAuthenticatedTypedEndpoint<LookupUserRequest, PublicUserProfile>
{
    private readonly UserService _users;

    public LookupUserEndpoint(UserService users) => _users = users;

    public string Route => "/api/users/lookup";
    public HttpMethod Method => HttpMethod.Post;

    public Task<PublicUserProfile> HandleAsync(
        UserSession session, LookupUserRequest request, CancellationToken cancellationToken) =>
        _users.LookupAsync(request.UserId, cancellationToken);
}
