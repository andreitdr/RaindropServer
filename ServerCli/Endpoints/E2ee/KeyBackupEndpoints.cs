using RaindropServer;
using RaindropServer.E2ee;
using RaindropServer.Users;

namespace ServerCli.Endpoints.E2ee;

/// <summary>GET /api/e2ee/backup — the caller's sealed key backup, which only their recovery key opens.</summary>
public sealed class GetKeyBackupEndpoint : IAuthenticatedEndpoint
{
    private readonly DeviceService _devices;

    public GetKeyBackupEndpoint(DeviceService devices) => _devices = devices;

    public string Route => "/api/e2ee/backup";
    public HttpMethod Method => HttpMethod.Get;

    public Task HandleAsync(UserSession session, HttpContext context, CancellationToken cancellationToken) =>
        EndpointResponse.WriteAsync(context, cancellationToken, ct => _devices.GetBackupAsync(session, ct));
}

/// <summary>POST /api/e2ee/backup/put — replaces the caller's sealed key backup. Needs a bound session.</summary>
public sealed class PutKeyBackupEndpoint
    : IAuthenticatedTypedEndpoint<PutKeyBackupRequest, KeyBackupResponse>
{
    private readonly DeviceService _devices;

    public PutKeyBackupEndpoint(DeviceService devices) => _devices = devices;

    public string Route => "/api/e2ee/backup/put";
    public HttpMethod Method => HttpMethod.Post;

    public Task<KeyBackupResponse> HandleAsync(UserSession session, PutKeyBackupRequest request, CancellationToken cancellationToken) =>
        _devices.PutBackupAsync(session, request, cancellationToken);
}
