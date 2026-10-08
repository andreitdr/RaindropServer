using Grpc.Core;
using RaindropServer.E2ee;
using RaindropServer.Grpc.Mapping;
using static RaindropServer.Grpc.Mapping.Wire;
using Proto = RaindropServer.Grpc.Protocol;

namespace RaindropServer.Grpc.Services;

/// <summary>
/// End-to-end encryption's identity layer; every rule is <see cref="DeviceService"/>'s. Every bytes field
/// is opaque here — produced and checked by clients' Rust core.
/// </summary>
public sealed class DeviceGrpcService : Proto.DeviceService.DeviceServiceBase
{
    private readonly DeviceService _devices;

    public DeviceGrpcService(DeviceService devices) => _devices = devices;

    public override async Task<Proto.RegisterDeviceResponse> RegisterDevice(
        Proto.RegisterDeviceRequest request, ServerCallContext context) =>
        new()
        {
            Device = (await _devices.RegisterDeviceAsync(context.Session(),
                new RegisterDeviceRequest(request.Certificate.ToByteArray()), context.CancellationToken)).ToProto()
        };

    public override async Task<Proto.ListDevicesResponse> ListDevices(Proto.ListDevicesRequest request, ServerCallContext context)
    {
        var devices = await _devices.ListDevicesAsync(context.Session(),
            new ListDevicesRequest(Ids(request.UserIds, "user_ids")), context.CancellationToken);

        return new Proto.ListDevicesResponse { Users = { devices.Users.Select(user => user.ToProto()) } };
    }

    public override async Task<Proto.RevokeDeviceResponse> RevokeDevice(Proto.RevokeDeviceRequest request, ServerCallContext context) =>
        new()
        {
            Message = (await _devices.RevokeDeviceAsync(context.Session(),
                new RevokeDeviceRequest(Id(request.DeviceId, "device_id")), context.CancellationToken)).Message
        };

    public override async Task<Proto.ResetIdentityResponse> ResetIdentity(
        Proto.ResetIdentityRequest request, ServerCallContext context) =>
        new()
        {
            Message = (await _devices.ResetIdentityAsync(context.Session(),
                new ResetIdentityRequest(request.Password), context.CancellationToken)).Message
        };

    public override async Task<Proto.CreateSessionChallengeResponse> CreateSessionChallenge(
        Proto.CreateSessionChallengeRequest request, ServerCallContext context)
    {
        var challenge = await _devices.CreateChallengeAsync(context.Session(), context.CancellationToken);
        return new Proto.CreateSessionChallengeResponse { Nonce = Bytes(challenge.Nonce), ExpiresAt = Time(challenge.ExpiresAtUtc) };
    }

    public override async Task<Proto.BindSessionResponse> BindSession(Proto.BindSessionRequest request, ServerCallContext context) =>
        new()
        {
            Message = (await _devices.BindSessionAsync(context.Session(),
                new BindSessionRequest(Id(request.DeviceId, "device_id"), request.Signature.ToByteArray()),
                context.CancellationToken)).Message
        };

    public override async Task<Proto.GetKeyBackupResponse> GetKeyBackup(Proto.GetKeyBackupRequest request, ServerCallContext context)
    {
        var backup = await _devices.GetBackupAsync(context.Session(), context.CancellationToken);

        var response = new Proto.GetKeyBackupResponse();
        if (backup.Blob is { } blob)
            response.Blob = Bytes(blob);
        if (backup.UpdatedAtUtc is { } updated)
            response.UpdatedAt = Time(updated);

        return response;
    }

    public override async Task<Proto.PutKeyBackupResponse> PutKeyBackup(Proto.PutKeyBackupRequest request, ServerCallContext context)
    {
        var backup = await _devices.PutBackupAsync(context.Session(),
            new PutKeyBackupRequest(request.Blob.ToByteArray()), context.CancellationToken);

        var response = new Proto.PutKeyBackupResponse();
        if (backup.Blob is { } blob)
            response.Blob = Bytes(blob);
        if (backup.UpdatedAtUtc is { } updated)
            response.UpdatedAt = Time(updated);

        return response;
    }

    public override async Task<Proto.StartDeviceLinkResponse> StartDeviceLink(
        Proto.StartDeviceLinkRequest request, ServerCallContext context)
    {
        var link = await _devices.StartLinkAsync(context.Session(),
            new StartDeviceLinkRequest(request.Offer.ToByteArray()), context.CancellationToken);

        return new Proto.StartDeviceLinkResponse { LinkId = link.LinkId, ExpiresAt = Time(link.ExpiresAtUtc) };
    }

    public override async Task<Proto.GetDeviceLinkOfferResponse> GetDeviceLinkOffer(
        Proto.GetDeviceLinkOfferRequest request, ServerCallContext context) =>
        new()
        {
            Offer = Bytes((await _devices.GetLinkOfferAsync(context.Session(),
                new DeviceLinkRequest(request.LinkId), context.CancellationToken)).Offer)
        };

    public override async Task<Proto.ApproveDeviceLinkResponse> ApproveDeviceLink(
        Proto.ApproveDeviceLinkRequest request, ServerCallContext context) =>
        new()
        {
            Message = (await _devices.ApproveLinkAsync(context.Session(),
                new ApproveDeviceLinkRequest(request.LinkId, request.Approval.ToByteArray()),
                context.CancellationToken)).Message
        };

    public override async Task<Proto.TakeDeviceLinkApprovalResponse> TakeDeviceLinkApproval(
        Proto.TakeDeviceLinkApprovalRequest request, ServerCallContext context)
    {
        var approval = await _devices.TakeLinkApprovalAsync(context.Session(),
            new DeviceLinkRequest(request.LinkId), context.CancellationToken);

        var response = new Proto.TakeDeviceLinkApprovalResponse();
        if (approval.Approval is { } bytes)
            response.Approval = Bytes(bytes);

        return response;
    }
}

/// <summary>The MLS delivery service; every rule is <see cref="MlsDeliveryService"/>'s.</summary>
public sealed class DeliveryGrpcService : Proto.DeliveryService.DeliveryServiceBase
{
    private readonly MlsDeliveryService _delivery;

    public DeliveryGrpcService(MlsDeliveryService delivery) => _delivery = delivery;

    public override async Task<Proto.CreateGroupResponse> CreateGroup(Proto.CreateGroupRequest request, ServerCallContext context) =>
        new()
        {
            State = (await _delivery.CreateGroupAsync(context.Session(),
                new CreateGroupRequest(Id(request.ChannelId, "channel_id"), request.GroupInfo.ToByteArray()),
                context.CancellationToken)).ToProto()
        };

    public override async Task<Proto.GetGroupStateResponse> GetGroupState(
        Proto.GetGroupStateRequest request, ServerCallContext context) =>
        new()
        {
            State = (await _delivery.GetStateAsync(context.Session(),
                new GroupRequest(Id(request.ChannelId, "channel_id")), context.CancellationToken)).ToProto()
        };

    public override async Task<Proto.SubmitCommitResponse> SubmitCommit(Proto.SubmitCommitRequest request, ServerCallContext context)
    {
        var accepted = await _delivery.SubmitCommitAsync(context.Session(),
            new SubmitCommitRequest(
                Id(request.ChannelId, "channel_id"),
                request.Commit.ToByteArray(),
                OptionalBytes(request.HasWelcome, request.Welcome),
                request.WelcomeRecipients.Select(Device).ToList()),
            context.CancellationToken);

        return new Proto.SubmitCommitResponse { Epoch = accepted.Epoch };
    }

    public override async Task<Proto.PutGroupInfoResponse> PutGroupInfo(Proto.PutGroupInfoRequest request, ServerCallContext context) =>
        new()
        {
            State = (await _delivery.PutGroupInfoAsync(context.Session(),
                new PutGroupInfoRequest(Id(request.ChannelId, "channel_id"), request.GroupInfo.ToByteArray()),
                context.CancellationToken)).ToProto()
        };

    public override async Task<Proto.SyncGroupResponse> SyncGroup(Proto.SyncGroupRequest request, ServerCallContext context)
    {
        var sync = await _delivery.SyncAsync(context.Session(),
            new SyncGroupRequest(Id(request.ChannelId, "channel_id"), request.FromEpoch), context.CancellationToken);

        return new Proto.SyncGroupResponse
        {
            Epoch = sync.Epoch,
            Commits = { sync.Commits.Select(commit => new Proto.GroupCommit { Epoch = commit.Epoch, Message = Bytes(commit.Message) }) },
            ResyncRequired = sync.ResyncRequired
        };
    }

    public override async Task<Proto.GetGroupRosterResponse> GetGroupRoster(
        Proto.GetGroupRosterRequest request, ServerCallContext context)
    {
        var roster = await _delivery.GetRosterAsync(context.Session(),
            new GroupRequest(Id(request.ChannelId, "channel_id")), context.CancellationToken);

        return new Proto.GetGroupRosterResponse
        {
            ChannelId = Text(roster.ChannelId),
            Devices = { roster.Devices.Select(device => device.ToProto()) }
        };
    }

    public override async Task<Proto.UploadKeyPackagesResponse> UploadKeyPackages(
        Proto.UploadKeyPackagesRequest request, ServerCallContext context)
    {
        var count = await _delivery.UploadKeyPackagesAsync(context.Session(),
            new UploadKeyPackagesRequest(
                request.KeyPackages.Select(package => package.ToByteArray()).ToList(),
                OptionalBytes(request.HasLastResort, request.LastResort)),
            context.CancellationToken);

        return new Proto.UploadKeyPackagesResponse { OneTime = count.OneTime };
    }

    public override async Task<Proto.ClaimKeyPackagesResponse> ClaimKeyPackages(
        Proto.ClaimKeyPackagesRequest request, ServerCallContext context)
    {
        var claimed = await _delivery.ClaimKeyPackagesAsync(context.Session(),
            new ClaimKeyPackagesRequest(Id(request.ChannelId, "channel_id"), request.Devices.Select(Device).ToList()),
            context.CancellationToken);

        return new Proto.ClaimKeyPackagesResponse
        {
            KeyPackages =
            {
                claimed.KeyPackages.Select(package => new Proto.ClaimedKeyPackage
                {
                    UserId = Text(package.UserId),
                    DeviceId = Text(package.DeviceId),
                    KeyPackage = Bytes(package.KeyPackage)
                })
            }
        };
    }

    public override async Task<Proto.GetKeyPackageCountResponse> GetKeyPackageCount(
        Proto.GetKeyPackageCountRequest request, ServerCallContext context) =>
        new() { OneTime = (await _delivery.CountKeyPackagesAsync(context.Session(), context.CancellationToken)).OneTime };

    public override async Task<Proto.ListWelcomesResponse> ListWelcomes(Proto.ListWelcomesRequest request, ServerCallContext context)
    {
        var welcomes = await _delivery.FetchWelcomesAsync(context.Session(), context.CancellationToken);
        return new Proto.ListWelcomesResponse { Welcomes = { welcomes.Welcomes.Select(welcome => welcome.ToProto()) } };
    }

    public override async Task<Proto.AckWelcomesResponse> AckWelcomes(Proto.AckWelcomesRequest request, ServerCallContext context) =>
        new()
        {
            Message = (await _delivery.AckWelcomesAsync(context.Session(),
                new AckWelcomesRequest(Ids(request.WelcomeIds, "welcome_ids")), context.CancellationToken)).Message
        };

    private static DeviceRef Device(Proto.DeviceRef device) =>
        new(Id(device.UserId, "user_id"), Id(device.DeviceId, "device_id"));
}
