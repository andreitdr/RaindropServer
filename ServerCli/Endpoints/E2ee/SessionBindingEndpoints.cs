using RaindropServer;
using RaindropServer.E2ee;
using RaindropServer.Users;

namespace ServerCli.Endpoints.E2ee;

/// <summary>POST /api/e2ee/session/challenge — a one-time nonce for this session to sign with a device key.</summary>
public sealed class SessionChallengeEndpoint : IAuthenticatedEndpoint
{
    private readonly DeviceService _devices;

    public SessionChallengeEndpoint(DeviceService devices) => _devices = devices;

    public string Route => "/api/e2ee/session/challenge";
    public HttpMethod Method => HttpMethod.Post;

    public Task HandleAsync(UserSession session, HttpContext context, CancellationToken cancellationToken) =>
        EndpointResponse.WriteAsync(context, cancellationToken, ct => _devices.CreateChallengeAsync(session, ct));
}

/// <summary>
/// POST /api/e2ee/session/bind — binds this session to one of the caller's devices, given the device
/// key's signature over the challenge.
/// </summary>
public sealed class BindSessionEndpoint
    : IAuthenticatedTypedEndpoint<BindSessionRequest, MessageResponse>
{
    private readonly DeviceService _devices;

    public BindSessionEndpoint(DeviceService devices) => _devices = devices;

    public string Route => "/api/e2ee/session/bind";
    public HttpMethod Method => HttpMethod.Post;

    public Task<MessageResponse> HandleAsync(UserSession session, BindSessionRequest request, CancellationToken cancellationToken) =>
        _devices.BindSessionAsync(session, request, cancellationToken);
}
