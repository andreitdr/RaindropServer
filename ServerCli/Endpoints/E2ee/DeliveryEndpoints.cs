using RaindropServer;
using RaindropServer.E2ee;
using RaindropServer.Users;

namespace ServerCli.Endpoints.E2ee;

// The MLS delivery service's HTTP surface. Every route needs a session bound to a device.

/// <summary>POST /api/e2ee/groups/create — starts a channel's MLS group from its creator's signed group info.</summary>
public sealed class CreateGroupEndpoint : IAuthenticatedTypedEndpoint<CreateGroupRequest, GroupStateResponse>
{
    private readonly MlsDeliveryService _delivery;

    public CreateGroupEndpoint(MlsDeliveryService delivery) => _delivery = delivery;

    public string Route => "/api/e2ee/groups/create";
    public HttpMethod Method => HttpMethod.Post;

    public Task<GroupStateResponse> HandleAsync(UserSession session, CreateGroupRequest request, CancellationToken cancellationToken) =>
        _delivery.CreateGroupAsync(session, request, cancellationToken);
}

/// <summary>POST /api/e2ee/groups/state — the group's current epoch and latest group info.</summary>
public sealed class GroupStateEndpoint : IAuthenticatedTypedEndpoint<GroupRequest, GroupStateResponse>
{
    private readonly MlsDeliveryService _delivery;

    public GroupStateEndpoint(MlsDeliveryService delivery) => _delivery = delivery;

    public string Route => "/api/e2ee/groups/state";
    public HttpMethod Method => HttpMethod.Post;

    public Task<GroupStateResponse> HandleAsync(UserSession session, GroupRequest request, CancellationToken cancellationToken) =>
        _delivery.GetStateAsync(session, request, cancellationToken);
}

/// <summary>POST /api/e2ee/groups/commit — accepts a commit made in the group's current epoch (else <c>epoch_conflict</c>) and fans it out.</summary>
public sealed class SubmitCommitEndpoint : IAuthenticatedTypedEndpoint<SubmitCommitRequest, SubmitCommitResponse>
{
    private readonly MlsDeliveryService _delivery;

    public SubmitCommitEndpoint(MlsDeliveryService delivery) => _delivery = delivery;

    public string Route => "/api/e2ee/groups/commit";
    public HttpMethod Method => HttpMethod.Post;

    public Task<SubmitCommitResponse> HandleAsync(UserSession session, SubmitCommitRequest request, CancellationToken cancellationToken) =>
        _delivery.SubmitCommitAsync(session, request, cancellationToken);
}

/// <summary>POST /api/e2ee/groups/info — publishes a group info for the group's current epoch.</summary>
public sealed class PutGroupInfoEndpoint : IAuthenticatedTypedEndpoint<PutGroupInfoRequest, GroupStateResponse>
{
    private readonly MlsDeliveryService _delivery;

    public PutGroupInfoEndpoint(MlsDeliveryService delivery) => _delivery = delivery;

    public string Route => "/api/e2ee/groups/info";
    public HttpMethod Method => HttpMethod.Post;

    public Task<GroupStateResponse> HandleAsync(UserSession session, PutGroupInfoRequest request, CancellationToken cancellationToken) =>
        _delivery.PutGroupInfoAsync(session, request, cancellationToken);
}

/// <summary>POST /api/e2ee/groups/sync — the commits a device needs to catch up from an epoch.</summary>
public sealed class SyncGroupEndpoint : IAuthenticatedTypedEndpoint<SyncGroupRequest, SyncGroupResponse>
{
    private readonly MlsDeliveryService _delivery;

    public SyncGroupEndpoint(MlsDeliveryService delivery) => _delivery = delivery;

    public string Route => "/api/e2ee/groups/sync";
    public HttpMethod Method => HttpMethod.Post;

    public Task<SyncGroupResponse> HandleAsync(UserSession session, SyncGroupRequest request, CancellationToken cancellationToken) =>
        _delivery.SyncAsync(session, request, cancellationToken);
}

/// <summary>POST /api/e2ee/groups/roster — every device that should be in the channel's group.</summary>
public sealed class GroupRosterEndpoint : IAuthenticatedTypedEndpoint<GroupRequest, GroupRosterResponse>
{
    private readonly MlsDeliveryService _delivery;

    public GroupRosterEndpoint(MlsDeliveryService delivery) => _delivery = delivery;

    public string Route => "/api/e2ee/groups/roster";
    public HttpMethod Method => HttpMethod.Post;

    public Task<GroupRosterResponse> HandleAsync(UserSession session, GroupRequest request, CancellationToken cancellationToken) =>
        _delivery.GetRosterAsync(session, request, cancellationToken);
}

/// <summary>POST /api/e2ee/keypackages/upload — stores key packages for this session's device.</summary>
public sealed class UploadKeyPackagesEndpoint : IAuthenticatedTypedEndpoint<UploadKeyPackagesRequest, KeyPackageCountResponse>
{
    private readonly MlsDeliveryService _delivery;

    public UploadKeyPackagesEndpoint(MlsDeliveryService delivery) => _delivery = delivery;

    public string Route => "/api/e2ee/keypackages/upload";
    public HttpMethod Method => HttpMethod.Post;

    public Task<KeyPackageCountResponse> HandleAsync(UserSession session, UploadKeyPackagesRequest request, CancellationToken cancellationToken) =>
        _delivery.UploadKeyPackagesAsync(session, request, cancellationToken);
}

/// <summary>POST /api/e2ee/keypackages/claim — one key package per device, for adding them to a channel's group.</summary>
public sealed class ClaimKeyPackagesEndpoint : IAuthenticatedTypedEndpoint<ClaimKeyPackagesRequest, ClaimKeyPackagesResponse>
{
    private readonly MlsDeliveryService _delivery;

    public ClaimKeyPackagesEndpoint(MlsDeliveryService delivery) => _delivery = delivery;

    public string Route => "/api/e2ee/keypackages/claim";
    public HttpMethod Method => HttpMethod.Post;

    public Task<ClaimKeyPackagesResponse> HandleAsync(UserSession session, ClaimKeyPackagesRequest request, CancellationToken cancellationToken) =>
        _delivery.ClaimKeyPackagesAsync(session, request, cancellationToken);
}

/// <summary>POST /api/e2ee/welcomes/ack — forgets Welcomes this device has processed.</summary>
public sealed class AckWelcomesEndpoint : IAuthenticatedTypedEndpoint<AckWelcomesRequest, MessageResponse>
{
    private readonly MlsDeliveryService _delivery;

    public AckWelcomesEndpoint(MlsDeliveryService delivery) => _delivery = delivery;

    public string Route => "/api/e2ee/welcomes/ack";
    public HttpMethod Method => HttpMethod.Post;

    public Task<MessageResponse> HandleAsync(UserSession session, AckWelcomesRequest request, CancellationToken cancellationToken) =>
        _delivery.AckWelcomesAsync(session, request, cancellationToken);
}

/// <summary>GET /api/e2ee/keypackages/count — how many one-time key packages this device has left.</summary>
public sealed class KeyPackageCountEndpoint : IAuthenticatedEndpoint
{
    private readonly MlsDeliveryService _delivery;

    public KeyPackageCountEndpoint(MlsDeliveryService delivery) => _delivery = delivery;

    public string Route => "/api/e2ee/keypackages/count";
    public HttpMethod Method => HttpMethod.Get;

    public Task HandleAsync(UserSession session, HttpContext context, CancellationToken cancellationToken) =>
        EndpointResponse.WriteAsync(context, cancellationToken, ct => _delivery.CountKeyPackagesAsync(session, ct));
}

/// <summary>GET /api/e2ee/welcomes — Welcomes waiting for this session's device.</summary>
public sealed class FetchWelcomesEndpoint : IAuthenticatedEndpoint
{
    private readonly MlsDeliveryService _delivery;

    public FetchWelcomesEndpoint(MlsDeliveryService delivery) => _delivery = delivery;

    public string Route => "/api/e2ee/welcomes";
    public HttpMethod Method => HttpMethod.Get;

    public Task HandleAsync(UserSession session, HttpContext context, CancellationToken cancellationToken) =>
        EndpointResponse.WriteAsync(context, cancellationToken, ct => _delivery.FetchWelcomesAsync(session, ct));
}
