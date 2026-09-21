using RaindropServer;
using RaindropServer.Users;

namespace ServerCli.Endpoints.Users;

/// <summary>
/// POST /api/users/status — the first call a client makes on connecting. The state it returns tells
/// the client whether to open the registration flow, ask for a password, or offer to rejoin a
/// preserved account.
/// </summary>
/// <remarks>
/// A POST for what is really a read, so that the UUID travels in an encrypted body rather than in a
/// URL, which is the one part of a request that tends to end up in logs.
/// </remarks>
public sealed class UserStateEndpoint : ITypedEndpoint<UserStateRequest, UserStateResponse>
{
    private readonly UserService _users;

    public UserStateEndpoint(UserService users) => _users = users;

    public string Route => "/api/users/status";
    public HttpMethod Method => HttpMethod.Post;

    public Task<UserStateResponse> HandleAsync(UserStateRequest request, CancellationToken cancellationToken) =>
        _users.GetStateAsync(request.UserId, cancellationToken);
}
