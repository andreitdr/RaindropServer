using RaindropServer;
using RaindropServer.E2ee;
using RaindropServer.Users;

namespace ServerCli.Endpoints.E2ee;

/// <summary>POST /api/e2ee/device-link/start — a new device opens a link for an existing one to approve.</summary>
public sealed class StartDeviceLinkEndpoint
    : IAuthenticatedTypedEndpoint<StartDeviceLinkRequest, StartDeviceLinkResponse>
{
    private readonly DeviceService _devices;

    public StartDeviceLinkEndpoint(DeviceService devices) => _devices = devices;

    public string Route => "/api/e2ee/device-link/start";
    public HttpMethod Method => HttpMethod.Post;

    public Task<StartDeviceLinkResponse> HandleAsync(UserSession session, StartDeviceLinkRequest request, CancellationToken cancellationToken) =>
        _devices.StartLinkAsync(session, request, cancellationToken);
}

/// <summary>POST /api/e2ee/device-link/offer — an existing device fetches the new device's offer.</summary>
public sealed class DeviceLinkOfferEndpoint
    : IAuthenticatedTypedEndpoint<DeviceLinkRequest, DeviceLinkOfferResponse>
{
    private readonly DeviceService _devices;

    public DeviceLinkOfferEndpoint(DeviceService devices) => _devices = devices;

    public string Route => "/api/e2ee/device-link/offer";
    public HttpMethod Method => HttpMethod.Post;

    public Task<DeviceLinkOfferResponse> HandleAsync(UserSession session, DeviceLinkRequest request, CancellationToken cancellationToken) =>
        _devices.GetLinkOfferAsync(session, request, cancellationToken);
}

/// <summary>POST /api/e2ee/device-link/approve — an existing device answers with an approval for the new one.</summary>
public sealed class ApproveDeviceLinkEndpoint
    : IAuthenticatedTypedEndpoint<ApproveDeviceLinkRequest, MessageResponse>
{
    private readonly DeviceService _devices;

    public ApproveDeviceLinkEndpoint(DeviceService devices) => _devices = devices;

    public string Route => "/api/e2ee/device-link/approve";
    public HttpMethod Method => HttpMethod.Post;

    public Task<MessageResponse> HandleAsync(UserSession session, ApproveDeviceLinkRequest request, CancellationToken cancellationToken) =>
        _devices.ApproveLinkAsync(session, request, cancellationToken);
}

/// <summary>
/// POST /api/e2ee/device-link/approval — the new device collects the approval, once; null until an
/// existing device has given it, so the new device polls.
/// </summary>
public sealed class TakeDeviceLinkApprovalEndpoint
    : IAuthenticatedTypedEndpoint<DeviceLinkRequest, DeviceLinkApprovalResponse>
{
    private readonly DeviceService _devices;

    public TakeDeviceLinkApprovalEndpoint(DeviceService devices) => _devices = devices;

    public string Route => "/api/e2ee/device-link/approval";
    public HttpMethod Method => HttpMethod.Post;

    public Task<DeviceLinkApprovalResponse> HandleAsync(UserSession session, DeviceLinkRequest request, CancellationToken cancellationToken) =>
        _devices.TakeLinkApprovalAsync(session, request, cancellationToken);
}
