using RaindropServer.Channels;
using RaindropServer.Events;
using RaindropServer.Roles;
using RaindropServer.Users;

namespace RaindropServer.E2ee;

/// <summary>
/// The MLS delivery service: orders, stores and fans out the traffic of channels' MLS groups, none
/// of which it can read. The server is never a group member.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>One commit per epoch.</b> A commit is accepted only if it was made in the group's
/// current epoch, and the database refuses a second one for the same epoch. The loser gets
/// <c>epoch_conflict</c>, processes the winning commit, and tries again, so every member sees the
/// same sequence of epochs and the group cannot fork.</item>
/// <item><b>Access is the membership policy's</b> (<see cref="IE2eeMembershipPolicy"/>). Somebody
/// who loses access stops receiving the group's traffic at once. Members are then told to
/// reconcile, and one of them commits the removal, which rotates the keys.</item>
/// <item><b>Only devices take part.</b> Every call needs a session bound to a device, so a password
/// alone cannot read a group's commits or claim anybody's key packages.</item>
/// <item>The server reads only the cleartext MLS envelope (<see cref="MlsFraming"/>): which group,
/// which epoch, which kind of message. That is enough to order the traffic without seeing any of
/// it.</item>
/// </list>
/// </remarks>
public sealed class MlsDeliveryService
{
    private readonly IMlsGroupStore _groups;
    private readonly IKeyPackageStore _keyPackages;
    private readonly IWelcomeStore _welcomes;
    private readonly IDeviceStore _devices;
    private readonly IChannelStore _channels;
    private readonly IUserStore _users;
    private readonly IE2eeMembershipPolicy _policy;
    private readonly IEventPublisher _events;
    private readonly E2eeOptions _options;
    private readonly TimeProvider _time;

    public MlsDeliveryService(
        IMlsGroupStore groups,
        IKeyPackageStore keyPackages,
        IWelcomeStore welcomes,
        IDeviceStore devices,
        IChannelStore channels,
        IUserStore users,
        IE2eeMembershipPolicy policy,
        IEventPublisher events,
        E2eeOptions options,
        TimeProvider? time = null)
    {
        _groups = groups;
        _keyPackages = keyPackages;
        _welcomes = welcomes;
        _devices = devices;
        _channels = channels;
        _users = users;
        _policy = policy;
        _events = events;
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    // ── Groups ───────────────────────────────────────────────────────────────

    /// <summary>Starts a channel's group, from its creator's signed group info.</summary>
    public async Task<GroupStateResponse> CreateGroupAsync(
        UserSession session, CreateGroupRequest request, CancellationToken cancellationToken)
    {
        var channel = await RequireAccessAsync(session, request.ChannelId, cancellationToken);
        var info = ParseGroupInfo(channel, request.GroupInfo);

        var group = new MlsGroupRecord(channel.ChannelId, (long)info.Epoch, request.GroupInfo, (long)info.Epoch, UtcNow);
        if (!await _groups.TryCreateAsync(group, cancellationToken))
            throw ApiException.Conflict("group_exists", "This channel already has a group; join it instead.");

        return ToState(group);
    }

    /// <summary>The group's epoch and latest group info.</summary>
    public async Task<GroupStateResponse> GetStateAsync(
        UserSession session, GroupRequest request, CancellationToken cancellationToken)
    {
        var channel = await RequireAccessAsync(session, request.ChannelId, cancellationToken);
        return ToState(await RequireGroupAsync(channel, cancellationToken));
    }

    /// <summary>
    /// Accepts a commit made in the group's current epoch and fans it out; stores its Welcome for the
    /// devices it adds. A commit for any other epoch gets <c>epoch_conflict</c>.
    /// </summary>
    public async Task<SubmitCommitResponse> SubmitCommitAsync(
        UserSession session, SubmitCommitRequest request, CancellationToken cancellationToken)
    {
        var channel = await RequireAccessAsync(session, request.ChannelId, cancellationToken);
        if (request.Commit is not { Length: > 0 } || request.Commit.Length > _options.MaximumMlsMessageBytes)
            throw ApiException.BadRequest("invalid_mls_message",
                $"A commit is 1 to {_options.MaximumMlsMessageBytes} bytes.");

        var envelope = MlsFraming.Parse(request.Commit);
        if (envelope.WireFormat is not (MlsWireFormat.PrivateMessage or MlsWireFormat.PublicMessage)
            || envelope.ContentType != MlsContentType.Commit)
            throw ApiException.BadRequest("invalid_mls_message", "That is not an MLS commit.");
        RequireGroupId(channel, envelope.GroupId);

        var recipients = request.WelcomeRecipients ?? [];
        if (request.Welcome is not null)
        {
            if (request.Welcome.Length > _options.MaximumMlsMessageBytes
                || MlsFraming.Parse(request.Welcome).WireFormat != MlsWireFormat.Welcome)
                throw ApiException.BadRequest("invalid_mls_message", "That is not an MLS Welcome.");
            if (recipients.Count is 0 || recipients.Count > _options.MaximumWelcomeRecipients)
                throw ApiException.BadRequest("invalid_welcome",
                    $"A Welcome names 1 to {_options.MaximumWelcomeRecipients} recipient devices.");
            foreach (var recipient in recipients)
                await RequireRosterDeviceAsync(channel, recipient, cancellationToken);
        }

        var group = await RequireGroupAsync(channel, cancellationToken);
        var epoch = (long)envelope.Epoch;
        var now = UtcNow;

        if (epoch != group.Epoch
            || !await _groups.TryAppendCommitAsync(
                new HandshakeRecord(channel.ChannelId, epoch, request.Commit, session.UserId, now), cancellationToken))
        {
            var current = (await _groups.FindAsync(channel.ChannelId, cancellationToken))?.Epoch ?? group.Epoch;
            throw ApiException.Conflict("epoch_conflict",
                $"The group is at epoch {current}; process the commits you are missing and try again.");
        }

        if (request.Welcome is not null)
        {
            await _welcomes.AddAsync(
                recipients.Select(r => new WelcomeRecord(
                    Guid.NewGuid(), r.UserId, r.DeviceId, channel.ChannelId, epoch + 1, request.Welcome, now)).ToList(),
                cancellationToken);
        }

        // Keep the log bounded; a device further behind than this rejoins by external commit.
        if (epoch - _options.MaximumLogEpochs >= 0)
            await _groups.PruneLogAsync(channel.ChannelId, epoch - _options.MaximumLogEpochs + 1, cancellationToken);

        await _events.PublishAsync(DeliveryEventTypes.Handshake,
            new HandshakeEvent(channel.ChannelId, epoch, request.Commit), AudienceFor(channel));
        foreach (var recipient in recipients)
            await _events.PublishAsync(DeliveryEventTypes.Welcome,
                new WelcomeEvent(channel.ChannelId, recipient.DeviceId), EventAudiences.Only(recipient.UserId));

        return new SubmitCommitResponse(epoch + 1);
    }

    /// <summary>Publishes a group info for the group's current epoch, for devices joining by themselves.</summary>
    public async Task<GroupStateResponse> PutGroupInfoAsync(
        UserSession session, PutGroupInfoRequest request, CancellationToken cancellationToken)
    {
        var channel = await RequireAccessAsync(session, request.ChannelId, cancellationToken);
        var info = ParseGroupInfo(channel, request.GroupInfo);
        var group = await RequireGroupAsync(channel, cancellationToken);

        if ((long)info.Epoch != group.Epoch
            || !await _groups.PutGroupInfoAsync(channel.ChannelId, (long)info.Epoch, request.GroupInfo, UtcNow, cancellationToken))
            throw ApiException.Conflict("epoch_conflict",
                $"The group is at epoch {group.Epoch}; a group info must describe that epoch.");

        return ToState(await RequireGroupAsync(channel, cancellationToken));
    }

    /// <summary>The commits a device needs to catch up from <see cref="SyncGroupRequest.FromEpoch"/>.</summary>
    public async Task<SyncGroupResponse> SyncAsync(
        UserSession session, SyncGroupRequest request, CancellationToken cancellationToken)
    {
        var channel = await RequireAccessAsync(session, request.ChannelId, cancellationToken);
        var group = await RequireGroupAsync(channel, cancellationToken);

        if (request.FromEpoch >= group.Epoch)
            return new SyncGroupResponse(group.Epoch, [], false);

        var oldest = await _groups.OldestLogEpochAsync(channel.ChannelId, cancellationToken);
        if (oldest is null || request.FromEpoch < oldest)
            return new SyncGroupResponse(group.Epoch, [], true);

        var commits = await _groups.ReadLogAsync(channel.ChannelId, request.FromEpoch, _options.SyncPageSize, cancellationToken);
        return new SyncGroupResponse(
            group.Epoch,
            commits.Select(c => new HandshakeSummary(c.Epoch, c.Message)).ToList(),
            false);
    }

    /// <summary>Every device that should be in the channel's group: what members reconcile the tree against.</summary>
    public async Task<GroupRosterResponse> GetRosterAsync(
        UserSession session, GroupRequest request, CancellationToken cancellationToken)
    {
        var channel = await RequireAccessAsync(session, request.ChannelId, cancellationToken);
        return new GroupRosterResponse(channel.ChannelId, await RosterAsync(channel, cancellationToken));
    }

    // ── Key packages ─────────────────────────────────────────────────────────

    /// <summary>Stores key packages for this session's device.</summary>
    public async Task<KeyPackageCountResponse> UploadKeyPackagesAsync(
        UserSession session, UploadKeyPackagesRequest request, CancellationToken cancellationToken)
    {
        var deviceId = await RequireDeviceAsync(session, cancellationToken);
        var oneTime = request.KeyPackages ?? [];

        if (oneTime.Count > _options.MaximumKeyPackagesPerUpload)
            throw ApiException.BadRequest("too_many_key_packages",
                $"Upload at most {_options.MaximumKeyPackagesPerUpload} key packages at once.");
        foreach (var package in oneTime.Append(request.LastResort).OfType<byte[]>())
        {
            if (package.Length is 0 || package.Length > _options.MaximumMlsMessageBytes
                || MlsFraming.Parse(package).WireFormat != MlsWireFormat.KeyPackage)
                throw ApiException.BadRequest("invalid_mls_message", "That is not an MLS key package.");
        }

        var stored = await _keyPackages.CountOneTimeAsync(session.UserId, deviceId, cancellationToken);
        if (stored + oneTime.Count > _options.MaximumKeyPackagesPerDevice)
            throw ApiException.Conflict("too_many_key_packages",
                $"A device may keep at most {_options.MaximumKeyPackagesPerDevice} key packages on the server.");

        var now = UtcNow;
        await _keyPackages.AddAsync(
            oneTime.Select(p => new KeyPackageRecord(Guid.NewGuid(), session.UserId, deviceId, p, false, now)).ToList(),
            cancellationToken);
        if (request.LastResort is not null)
            await _keyPackages.ReplaceLastResortAsync(
                new KeyPackageRecord(Guid.NewGuid(), session.UserId, deviceId, request.LastResort, true, now),
                cancellationToken);

        return new KeyPackageCountResponse(await _keyPackages.CountOneTimeAsync(session.UserId, deviceId, cancellationToken));
    }

    /// <summary>How many one-time key packages this session's device has left.</summary>
    public async Task<KeyPackageCountResponse> CountKeyPackagesAsync(UserSession session, CancellationToken cancellationToken)
    {
        var deviceId = await RequireDeviceAsync(session, cancellationToken);
        return new KeyPackageCountResponse(await _keyPackages.CountOneTimeAsync(session.UserId, deviceId, cancellationToken));
    }

    /// <summary>
    /// Hands out one key package per device, for adding them to a channel's group, and only to
    /// somebody who is in it. A device that does not belong in the group (never did, or was revoked
    /// or lost access since the caller read the roster) is skipped rather than failing the others:
    /// nothing is handed out for it either way, and the caller adds everyone else.
    /// </summary>
    public async Task<ClaimKeyPackagesResponse> ClaimKeyPackagesAsync(
        UserSession session, ClaimKeyPackagesRequest request, CancellationToken cancellationToken)
    {
        var channel = await RequireAccessAsync(session, request.ChannelId, cancellationToken);
        var devices = (request.Devices ?? []).Distinct().ToList();
        if (devices.Count > _options.MaximumWelcomeRecipients)
            throw ApiException.BadRequest("too_many_devices",
                $"Claim for at most {_options.MaximumWelcomeRecipients} devices at once.");

        var claimed = new List<ClaimedKeyPackage>();
        foreach (var device in devices)
        {
            if (!await BelongsAsync(channel, device, cancellationToken))
                continue;

            var package = await _keyPackages.ClaimAsync(device.UserId, device.DeviceId, cancellationToken);
            if (package is null)
                continue;
            claimed.Add(new ClaimedKeyPackage(device.UserId, device.DeviceId, package.KeyPackage));

            var remaining = await _keyPackages.CountOneTimeAsync(device.UserId, device.DeviceId, cancellationToken);
            if (remaining < _options.KeyPackageLowWatermark)
                await _events.PublishAsync(DeliveryEventTypes.KeyPackagesLow,
                    new KeyPackagesLowEvent(device.DeviceId, remaining), EventAudiences.Only(device.UserId));
        }

        return new ClaimKeyPackagesResponse(claimed);
    }

    // ── Welcomes ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Welcomes waiting for this session's device. Only for groups the user is still allowed in: a
    /// Welcome to a channel they have since lost is withheld.
    /// </summary>
    public async Task<WelcomesResponse> FetchWelcomesAsync(UserSession session, CancellationToken cancellationToken)
    {
        var deviceId = await RequireDeviceAsync(session, cancellationToken);
        var pending = await _welcomes.ListAsync(session.UserId, deviceId, cancellationToken);

        var visible = new List<PendingWelcome>();
        foreach (var welcome in pending)
        {
            if (await _channels.FindByIdAsync(welcome.ChannelId, cancellationToken) is { } channel
                && await _policy.IsAllowedAsync(session.UserId, channel, cancellationToken))
                visible.Add(new PendingWelcome(welcome.WelcomeId, welcome.ChannelId, welcome.Epoch, welcome.Welcome));
        }

        return new WelcomesResponse(visible);
    }

    /// <summary>Forgets Welcomes this device has processed.</summary>
    public async Task<MessageResponse> AckWelcomesAsync(
        UserSession session, AckWelcomesRequest request, CancellationToken cancellationToken)
    {
        var deviceId = await RequireDeviceAsync(session, cancellationToken);
        await _welcomes.DeleteAsync(session.UserId, deviceId, request.WelcomeIds ?? [], cancellationToken);
        return new MessageResponse("Acknowledged.");
    }

    // ── For the reconciler ───────────────────────────────────────────────────

    /// <summary>Tells everybody in every group to compare its tree with the roster.</summary>
    public async Task RequestReconcileAllAsync(CancellationToken cancellationToken)
    {
        foreach (var channelId in await _groups.ListChannelIdsAsync(cancellationToken))
        {
            if (await _channels.FindByIdAsync(channelId, cancellationToken) is { } channel)
                await _events.PublishAsync(DeliveryEventTypes.GroupReconcile,
                    new GroupReconcileEvent(channelId), AudienceFor(channel));
        }
    }

    /// <summary>Drops what a revoked device had waiting: its key packages and Welcomes.</summary>
    public async Task ForgetDeviceAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken)
    {
        await _keyPackages.DeleteForDeviceAsync(userId, deviceId, cancellationToken);
        await _welcomes.DeleteForDeviceAsync(userId, deviceId, cancellationToken);
    }

    /// <summary>Drops a deleted channel's group, log and Welcomes.</summary>
    public Task ForgetGroupAsync(Guid channelId, CancellationToken cancellationToken) =>
        _groups.DeleteAsync(channelId, cancellationToken);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<DeviceRef>> RosterAsync(ChannelRecord channel, CancellationToken cancellationToken)
    {
        var allowed = new List<Guid>();
        foreach (var user in await _users.ListByStatusAsync(UserStatus.Active, cancellationToken))
        {
            if (await _policy.IsAllowedAsync(user.UserId, channel, cancellationToken))
                allowed.Add(user.UserId);
        }

        var devices = await _devices.ListDevicesAsync(allowed, includeRevoked: false, cancellationToken);
        return devices.Select(d => new DeviceRef(d.UserId, d.DeviceId)).ToList();
    }

    private async Task<bool> BelongsAsync(ChannelRecord channel, DeviceRef device, CancellationToken cancellationToken)
    {
        var record = await _devices.FindDeviceAsync(device.UserId, device.DeviceId, cancellationToken);
        return record is { IsActive: true } && await _policy.IsAllowedAsync(device.UserId, channel, cancellationToken);
    }

    private async Task RequireRosterDeviceAsync(ChannelRecord channel, DeviceRef device, CancellationToken cancellationToken)
    {
        if (!await BelongsAsync(channel, device, cancellationToken))
            throw ApiException.Forbidden("not_allowed_in_group",
                "That device does not belong in this channel's group.");
    }

    /// <summary>
    /// The caller's channel, if they are allowed in its group and calling from one of their devices.
    /// A channel they may not see is reported as not found, as everywhere else.
    /// </summary>
    private async Task<ChannelRecord> RequireAccessAsync(UserSession session, Guid channelId, CancellationToken cancellationToken)
    {
        await RequireDeviceAsync(session, cancellationToken);

        var channel = await _channels.FindByIdAsync(channelId, cancellationToken);
        if (channel is null || !await _policy.IsAllowedAsync(session.UserId, channel, cancellationToken))
            throw ApiException.NotFound("channel_not_found", "No channel with that id exists, or you cannot see it.");

        return channel;
    }

    private async Task<Guid> RequireDeviceAsync(UserSession session, CancellationToken cancellationToken)
    {
        if (session.DeviceId is not { } deviceId)
            throw ApiException.Forbidden("device_required",
                "End-to-end encrypted groups need a session bound to one of your devices.");

        // Revoking a device ends its sessions, so this is belt and braces: never serve a revoked one.
        var device = await _devices.FindDeviceAsync(session.UserId, deviceId, cancellationToken);
        if (device is null || !device.IsActive)
            throw ApiException.Unauthorized("invalid_session", "This device has been revoked.");

        return deviceId;
    }

    private async Task<MlsGroupRecord> RequireGroupAsync(ChannelRecord channel, CancellationToken cancellationToken) =>
        await _groups.FindAsync(channel.ChannelId, cancellationToken)
        ?? throw ApiException.NotFound("group_not_found", "This channel has no group yet; create it.");

    private MlsEnvelope ParseGroupInfo(ChannelRecord channel, byte[]? groupInfo)
    {
        if (groupInfo is not { Length: > 0 } || groupInfo.Length > _options.MaximumMlsMessageBytes)
            throw ApiException.BadRequest("invalid_mls_message",
                $"A group info is 1 to {_options.MaximumMlsMessageBytes} bytes.");

        var envelope = MlsFraming.Parse(groupInfo);
        if (envelope.WireFormat != MlsWireFormat.GroupInfo)
            throw ApiException.BadRequest("invalid_mls_message", "That is not an MLS group info.");
        RequireGroupId(channel, envelope.GroupId);
        return envelope;
    }

    private static void RequireGroupId(ChannelRecord channel, byte[]? groupId)
    {
        if (groupId is null || !groupId.AsSpan().SequenceEqual(MlsFraming.GroupIdFor(channel.ChannelId)))
            throw ApiException.BadRequest("wrong_group", "That message is for a different channel's group.");
    }

    private EventAudience AudienceFor(ChannelRecord channel) =>
        async (userId, cancellationToken) => await _policy.IsAllowedAsync(userId, channel, cancellationToken);

    private static GroupStateResponse ToState(MlsGroupRecord group) =>
        new(group.ChannelId, group.Epoch, group.GroupInfo, group.GroupInfoEpoch);
}

/// <summary>
/// Keeps groups in step with who is allowed in them: when roles, memberships or devices change,
/// members are told to reconcile, and a revoked device's key packages and Welcomes are dropped.
/// The server cannot change a group itself; members commit the difference.
/// </summary>
internal sealed class GroupMembershipReconciler :
    IDeviceListener,
    IServerEventHandler<RoleSummary>,
    IServerEventHandler<RoleDeletedEvent>,
    IServerEventHandler<MemberRolesEvent>,
    IServerEventHandler<MemberEvent>,
    IServerEventHandler<ChannelDeletedEvent>
{
    private readonly MlsDeliveryService _delivery;

    public GroupMembershipReconciler(MlsDeliveryService delivery) => _delivery = delivery;

    public Task OnDeviceAddedAsync(DeviceRecord device, CancellationToken cancellationToken) =>
        _delivery.RequestReconcileAllAsync(cancellationToken);

    public async Task OnDeviceRevokedAsync(DeviceRecord device, CancellationToken cancellationToken)
    {
        await _delivery.ForgetDeviceAsync(device.UserId, device.DeviceId, cancellationToken);
        await _delivery.RequestReconcileAllAsync(cancellationToken);
    }

    public Task OnIdentityResetAsync(Guid userId, CancellationToken cancellationToken) =>
        _delivery.RequestReconcileAllAsync(cancellationToken);

    public Task HandleAsync(ServerEvent<RoleSummary> serverEvent, CancellationToken cancellationToken) =>
        _delivery.RequestReconcileAllAsync(cancellationToken);

    public Task HandleAsync(ServerEvent<RoleDeletedEvent> serverEvent, CancellationToken cancellationToken) =>
        _delivery.RequestReconcileAllAsync(cancellationToken);

    public Task HandleAsync(ServerEvent<MemberRolesEvent> serverEvent, CancellationToken cancellationToken) =>
        _delivery.RequestReconcileAllAsync(cancellationToken);

    public Task HandleAsync(ServerEvent<MemberEvent> serverEvent, CancellationToken cancellationToken) =>
        serverEvent.Type is ServerEventTypes.MemberLeft or ServerEventTypes.MemberJoined
            ? _delivery.RequestReconcileAllAsync(cancellationToken)
            : Task.CompletedTask;

    public Task HandleAsync(ServerEvent<ChannelDeletedEvent> serverEvent, CancellationToken cancellationToken) =>
        _delivery.ForgetGroupAsync(serverEvent.Data.ChannelId, cancellationToken);
}
