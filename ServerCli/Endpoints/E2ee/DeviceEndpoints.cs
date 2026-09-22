using RaindropServer;
using RaindropServer.E2ee;
using RaindropServer.Users;

namespace ServerCli.Endpoints.E2ee;

/// <summary>
/// POST /api/e2ee/devices/register — registers one of the caller's devices from its certificate. The
/// first device a user registers establishes their identity key.
/// </summary>
public sealed class RegisterDeviceEndpoint
    : IAuthenticatedTypedEndpoint<RegisterDeviceRequest, DeviceSummary>
{
    private readonly DeviceService _devices;

    public RegisterDeviceEndpoint(DeviceService devices) => _devices = devices;

    public string Route => "/api/e2ee/devices/register";
    public HttpMethod Method => HttpMethod.Post;

    public Task<DeviceSummary> HandleAsync(UserSession session, RegisterDeviceRequest request, CancellationToken cancellationToken) =>
        _devices.RegisterDeviceAsync(session, request, cancellationToken);
}

/// <summary>POST /api/e2ee/devices/list — identity keys and active devices of the given users.</summary>
public sealed class ListDevicesEndpoint
    : IAuthenticatedTypedEndpoint<ListDevicesRequest, ListDevicesResponse>
{
    private readonly DeviceService _devices;

    public ListDevicesEndpoint(DeviceService devices) => _devices = devices;

    public string Route => "/api/e2ee/devices/list";
    public HttpMethod Method => HttpMethod.Post;

    public Task<ListDevicesResponse> HandleAsync(UserSession session, ListDevicesRequest request, CancellationToken cancellationToken) =>
        _devices.ListDevicesAsync(session, request, cancellationToken);
}

/// <summary>POST /api/e2ee/devices/revoke — revokes one of the caller's devices and ends its sessions.</summary>
public sealed class RevokeDeviceEndpoint
    : IAuthenticatedTypedEndpoint<RevokeDeviceRequest, MessageResponse>
{
    private readonly DeviceService _devices;

    public RevokeDeviceEndpoint(DeviceService devices) => _devices = devices;

    public string Route => "/api/e2ee/devices/revoke";
    public HttpMethod Method => HttpMethod.Post;

    public Task<MessageResponse> HandleAsync(UserSession session, RevokeDeviceRequest request, CancellationToken cancellationToken) =>
        _devices.RevokeDeviceAsync(session, request, cancellationToken);
}

/// <summary>
/// POST /api/e2ee/identity/reset — forgets the caller's identity key and backup and revokes every
/// device. Needs the password. Everyone is told, because the caller's safety number changes.
/// </summary>
public sealed class ResetIdentityEndpoint
    : IAuthenticatedTypedEndpoint<ResetIdentityRequest, MessageResponse>
{
    private readonly DeviceService _devices;

    public ResetIdentityEndpoint(DeviceService devices) => _devices = devices;

    public string Route => "/api/e2ee/identity/reset";
    public HttpMethod Method => HttpMethod.Post;

    public Task<MessageResponse> HandleAsync(UserSession session, ResetIdentityRequest request, CancellationToken cancellationToken) =>
        _devices.ResetIdentityAsync(session, request, cancellationToken);
}
