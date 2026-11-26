using System.Security.Cryptography;
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
/// <item><b>A broken group is replaced, not repaired.</b> The server cannot read a commit, so it
/// cannot refuse one that no member can process. A member that cannot get past a commit (or join
/// from the published group info) reports it, and the channel's group starts a new generation — a
/// new MLS group, with a new group id, that a member creates and everyone else joins with every
/// member validated as always (<see cref="ResetGroupAsync"/>). Every commit is recorded with the
/// device that submitted it, so a broken one is attributed, and a channel's managers can keep that
/// device, or its account, out of the group (<see cref="BanAsync"/>).</item>
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
    private readonly IPermissionResolver _permissions;
    private readonly IEventPublisher _events;
    private readonly E2eeOptions _options;
    private readonly TimeProvider _time;

    /// <summary>
    /// Serialises what changes which group a channel has — creating it, resetting it, and the
    /// commits and group infos that must belong to the generation they were checked against — per
    /// channel. Striped, so the set never grows; a Raindrop server is one process.
    /// </summary>
    private readonly SemaphoreSlim[] _groupLocks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public MlsDeliveryService(
        IMlsGroupStore groups,
        IKeyPackageStore keyPackages,
        IWelcomeStore welcomes,
        IDeviceStore devices,
        IChannelStore channels,
        IUserStore users,
        IE2eeMembershipPolicy policy,
        IPermissionResolver permissions,
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
        _permissions = permissions;
        _events = events;
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    // ── Groups ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a channel's group, from its creator's signed group info, in the group's current
    /// generation. Just after a reset, only the device that asked for it may (for
    /// <see cref="E2eeOptions.GroupCreatorReservation"/>): <c>group_reserved</c> to anybody else.
    /// </summary>
    public async Task<GroupStateResponse> CreateGroupAsync(
        UserSession session, CreateGroupRequest request, CancellationToken cancellationToken)
    {
        var channel = await RequireAccessAsync(session, request.ChannelId, cancellationToken);
        var deviceId = session.DeviceId!.Value;

        return await WithGroupLockAsync(channel.ChannelId, async () =>
        {
            var generation = await _groups.GenerationAsync(channel.ChannelId, cancellationToken);
            var info = ParseGroupInfo(channel, generation, request.GroupInfo);

            if (generation > 0
                && (await _groups.ListResetsAsync(channel.ChannelId, 1, cancellationToken)) is [{ } reset]
                && reset.Generation == generation
                && reset.RequestedByDeviceId != deviceId
                && UtcNow < reset.ResetAtUtc + _options.GroupCreatorReservation
                && await _groups.FindAsync(channel.ChannelId, cancellationToken) is null)
                throw ApiException.Conflict("group_reserved",
                    "The member who reset this channel's group is starting it again; join it in a moment.");

            var group = new MlsGroupRecord(
                channel.ChannelId, (long)info.Epoch, request.GroupInfo, (long)info.Epoch, UtcNow, generation);
            if (!await _groups.TryCreateAsync(group, cancellationToken))
                throw ApiException.Conflict("group_exists", "This channel already has a group; join it instead.");

            return ToState(group);
        }, cancellationToken);
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
        var generation = await _groups.GenerationAsync(channel.ChannelId, cancellationToken);
        RequireGroupId(channel, generation, envelope.GroupId);

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

        var epoch = (long)envelope.Epoch;
        var now = UtcNow;

        await WithGroupLockAsync(channel.ChannelId, async () =>
        {
            var group = await RequireGroupAsync(channel, cancellationToken);
            // Checked against the generation read above: a reset in between makes this a commit for
            // a group that is gone.
            if (group.Generation != generation)
                throw ApiException.BadRequest("wrong_group", "That commit is for an earlier generation of this channel's group.");

            // Recorded with the device whose bound session submitted it, so a commit nobody can
            // process can be attributed (ResetGroupAsync).
            if (epoch != group.Epoch
                || !await _groups.TryAppendCommitAsync(
                    new HandshakeRecord(channel.ChannelId, epoch, request.Commit, session.UserId, now, session.DeviceId),
                    cancellationToken))
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

            return true;
        }, cancellationToken);

        await _events.PublishAsync(DeliveryEventTypes.Handshake,
            new HandshakeEvent(channel.ChannelId, epoch, request.Commit), await AudienceForAsync(channel, cancellationToken));
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

        return await WithGroupLockAsync(channel.ChannelId, async () =>
        {
            var group = await RequireGroupAsync(channel, cancellationToken);
            var info = ParseGroupInfo(channel, group.Generation, request.GroupInfo);

            if ((long)info.Epoch != group.Epoch
                || !await _groups.PutGroupInfoAsync(channel.ChannelId, (long)info.Epoch, request.GroupInfo, UtcNow, cancellationToken))
                throw ApiException.Conflict("epoch_conflict",
                    $"The group is at epoch {group.Epoch}; a group info must describe that epoch.");

            return ToState(await RequireGroupAsync(channel, cancellationToken));
        }, cancellationToken);
    }

    /// <summary>The commits a device needs to catch up from <see cref="SyncGroupRequest.FromEpoch"/>.</summary>
    public async Task<SyncGroupResponse> SyncAsync(
        UserSession session, SyncGroupRequest request, CancellationToken cancellationToken)
    {
        var channel = await RequireAccessAsync(session, request.ChannelId, cancellationToken);
        var found = await _groups.FindAsync(channel.ChannelId, cancellationToken);
        var generation = found?.Generation ?? await _groups.GenerationAsync(channel.ChannelId, cancellationToken);

        // A device whose group is of another generation cannot follow this log at all: it rejoins,
        // in the generation it is told.
        if (request.Generation != generation)
            return new SyncGroupResponse(found?.Epoch ?? 0, [], true, generation);

        var group = found ?? throw ApiException.NotFound("group_not_found", "This channel has no group yet; create it.");
        if (request.FromEpoch >= group.Epoch)
            return new SyncGroupResponse(group.Epoch, [], false, generation);

        var oldest = await _groups.OldestLogEpochAsync(channel.ChannelId, cancellationToken);
        if (oldest is null || request.FromEpoch < oldest)
            return new SyncGroupResponse(group.Epoch, [], true, generation);

        var commits = await _groups.ReadLogAsync(channel.ChannelId, request.FromEpoch, _options.SyncPageSize, cancellationToken);
        return new SyncGroupResponse(
            group.Epoch,
            commits.Select(c => new HandshakeSummary(c.Epoch, c.Message)).ToList(),
            false,
            generation);
    }

    /// <summary>Every device that should be in the channel's group: what members reconcile the tree against.</summary>
    public async Task<GroupRosterResponse> GetRosterAsync(
        UserSession session, GroupRequest request, CancellationToken cancellationToken)
    {
        var channel = await RequireAccessAsync(session, request.ChannelId, cancellationToken);
        return new GroupRosterResponse(channel.ChannelId, await RosterAsync(channel, cancellationToken));
    }

    // ── Generations ──────────────────────────────────────────────────────────

    /// <summary>The channel group's current generation, whether or not that generation's group exists yet.</summary>
    public async Task<GroupGenerationResponse> GetGenerationAsync(
        UserSession session, GroupRequest request, CancellationToken cancellationToken)
    {
        var channel = await RequireAccessAsync(session, request.ChannelId, cancellationToken);
        return new GroupGenerationResponse(channel.ChannelId, await _groups.GenerationAsync(channel.ChannelId, cancellationToken));
    }

    /// <summary>
    /// Starts a new generation of a channel's group, because its current one can no longer be
    /// followed: a commit nobody can process (garbage, or a member that does not verify), or a group
    /// info nobody can join from. The previous generation's group, log and Welcomes go; history
    /// already decrypted stays in members' local caches.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><b>Who.</b> Any member of the group (the same access as every call here), reporting
    /// what it cannot get past: the SHA-256 of a commit in the current log, or of the published group
    /// info. The server cannot tell whether it is really broken — it cannot read either — so the
    /// report is what makes a reset accountable: it is recorded with who asked and, for a commit, the
    /// account and device that submitted it. A channel manager (<c>delete_channels</c>, which can
    /// already delete the channel and its group outright) may reset without a report.</item>
    /// <item><b>Once per generation.</b> A request for a generation that is already behind changes
    /// nothing and answers the current one, so members who found the same broken commit end up in
    /// one new generation, not one each.</item>
    /// <item><b>Rate limited.</b> One reset per member per channel per
    /// <see cref="E2eeOptions.GroupResetCooldown"/> (<c>rate_limited</c>); managers are not limited.
    /// A group info that has not caught up with the epoch can only be reported once it has lagged
    /// for <see cref="E2eeOptions.GroupInfoGrace"/> (<c>group_info_pending</c>).</item>
    /// <item><b>Nothing is trusted that was not before.</b> The new generation's group is created by a
    /// member and joined by external commit or Welcome, every member validated by every device as
    /// always; for <see cref="E2eeOptions.GroupCreatorReservation"/> only the device that reset may
    /// create it.</item>
    /// </list>
    /// </remarks>
    public async Task<ResetGroupResponse> ResetGroupAsync(
        UserSession session, ResetGroupRequest request, CancellationToken cancellationToken)
    {
        var channel = await RequireAccessAsync(session, request.ChannelId, cancellationToken);
        var deviceId = session.DeviceId!.Value;
        if (request.ReportedSha256 is { } digest && digest.Length != SHA256.HashSizeInBytes)
            throw ApiException.BadRequest("invalid_payload", "A report names a SHA-256 hash: 32 bytes.");

        var manager = (await _permissions.GetAsync(session.UserId, channel, cancellationToken)).Allows(Permission.DeleteChannels);
        var now = UtcNow;

        var response = await WithGroupLockAsync(channel.ChannelId, async () =>
        {
            var current = await _groups.GenerationAsync(channel.ChannelId, cancellationToken);
            if (request.Generation < current)
                return new ResetGroupResponse(current, false);
            if (request.Generation > current)
                throw ApiException.BadRequest("invalid_payload", $"This channel's group is in generation {current}.");

            string? kind = null;
            Guid? senderUserId = null, senderDeviceId = null;
            if (request.ReportedSha256 is { } reported)
            {
                var group = await _groups.FindAsync(channel.ChannelId, cancellationToken);
                var log = await _groups.ReadLogAsync(channel.ChannelId, request.ReportedEpoch, 1, cancellationToken);
                if (log is [{ } commit] && commit.Epoch == request.ReportedEpoch
                    && SHA256.HashData(commit.Message).AsSpan().SequenceEqual(reported))
                {
                    kind = GroupResetReports.Commit;
                    senderUserId = commit.SenderUserId;
                    senderDeviceId = commit.SenderDeviceId;
                }
                else if (group?.GroupInfo is { } info && group.GroupInfoEpoch == request.ReportedEpoch
                         && SHA256.HashData(info).AsSpan().SequenceEqual(reported))
                {
                    // A group info behind the epoch is normal for a moment: its committer publishes
                    // the next one right after committing.
                    if (!manager && group.GroupInfoEpoch < group.Epoch && now < group.UpdatedAtUtc + _options.GroupInfoGrace)
                        throw ApiException.Conflict("group_info_pending",
                            "A newer group info should be published shortly; try joining again in a moment.");
                    kind = GroupResetReports.GroupInfo;
                }
                else
                {
                    throw ApiException.Conflict("report_mismatch",
                        "That is neither a commit in this channel group's log nor its published group info.");
                }
            }
            else if (!manager)
            {
                throw ApiException.Forbidden("missing_permission",
                    "You do not have permission to reset this channel's encryption group without reporting what is broken.");
            }

            if (!manager && _options.GroupResetCooldown > TimeSpan.Zero
                && await _groups.LatestResetByAsync(channel.ChannelId, session.UserId, cancellationToken) is { } last
                && now < last.ResetAtUtc + _options.GroupResetCooldown)
                throw ApiException.TooLarge("rate_limited",
                    "You reset this channel's encryption group a moment ago; wait a little before resetting it again.");

            var reset = new MlsGroupResetRecord(
                channel.ChannelId, current + 1, now, session.UserId, deviceId, kind,
                kind is null ? null : request.ReportedEpoch, request.ReportedSha256, senderUserId, senderDeviceId);
            return await _groups.TryResetAsync(reset, cancellationToken)
                ? new ResetGroupResponse(current + 1, true)
                : new ResetGroupResponse(await _groups.GenerationAsync(channel.ChannelId, cancellationToken), false);
        }, cancellationToken);

        if (response.Reset)
            await _events.PublishAsync(DeliveryEventTypes.GroupReset,
                new GroupResetEvent(channel.ChannelId, response.Generation), await AudienceForAsync(channel, cancellationToken));
        return response;
    }

    /// <summary>Channel managers: every reset of the channel's group, newest first, with its attribution.</summary>
    public async Task<GroupResetsResponse> ListResetsAsync(
        UserSession session, GroupRequest request, CancellationToken cancellationToken)
    {
        var channel = await RequireManagerAsync(session, request.ChannelId, cancellationToken);
        return new GroupResetsResponse(await _groups.ListResetsAsync(channel.ChannelId, 100, cancellationToken));
    }

    // ── Bans ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Channel managers: keeps a device, or every device of an account, out of the channel's group.
    /// The server cannot remove it from the group itself; it leaves the roster, can no longer call
    /// anything here for the channel or be welcomed into it, and members are told to reconcile, which
    /// commits its removal and rotates the keys past it.
    /// </summary>
    public async Task<GroupBanChangedResponse> BanAsync(
        UserSession session, GroupBanRequest request, CancellationToken cancellationToken)
    {
        var channel = await RequireManagerAsync(session, request.ChannelId, cancellationToken);
        if (request.UserId == Guid.Empty)
            throw ApiException.BadRequest("missing_identifier", "Name the account to keep out of the group.");

        var changed = await _groups.TryBanAsync(
            new MlsGroupBanRecord(channel.ChannelId, request.UserId, request.DeviceId, session.UserId, UtcNow), cancellationToken);
        if (changed)
            await _events.PublishAsync(DeliveryEventTypes.GroupReconcile,
                new GroupReconcileEvent(channel.ChannelId), await AudienceForAsync(channel, cancellationToken));
        return new GroupBanChangedResponse(changed);
    }

    /// <summary>Channel managers: lifts a ban; the device may join the group again.</summary>
    public async Task<GroupBanChangedResponse> UnbanAsync(
        UserSession session, GroupBanRequest request, CancellationToken cancellationToken)
    {
        var channel = await RequireManagerAsync(session, request.ChannelId, cancellationToken);
        var changed = await _groups.UnbanAsync(channel.ChannelId, request.UserId, request.DeviceId, cancellationToken);
        if (changed)
            await _events.PublishAsync(DeliveryEventTypes.GroupReconcile,
                new GroupReconcileEvent(channel.ChannelId), await AudienceForAsync(channel, cancellationToken));
        return new GroupBanChangedResponse(changed);
    }

    /// <summary>Channel managers: the bans on the channel's group.</summary>
    public async Task<GroupBansResponse> ListBansAsync(
        UserSession session, GroupRequest request, CancellationToken cancellationToken)
    {
        var channel = await RequireManagerAsync(session, request.ChannelId, cancellationToken);
        return new GroupBansResponse(await _groups.ListBansAsync(channel.ChannelId, cancellationToken));
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
                && await _policy.IsAllowedAsync(session.UserId, channel, cancellationToken)
                && !await _groups.IsBannedAsync(channel.ChannelId, session.UserId, deviceId, cancellationToken))
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
                    new GroupReconcileEvent(channelId), await AudienceForAsync(channel, cancellationToken));
        }
    }

    /// <summary>Drops what a revoked device had waiting: its key packages and Welcomes.</summary>
    public async Task ForgetDeviceAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken)
    {
        await _keyPackages.DeleteForDeviceAsync(userId, deviceId, cancellationToken);
        await _welcomes.DeleteForDeviceAsync(userId, deviceId, cancellationToken);
    }

    /// <summary>Drops a deleted channel's group, log, Welcomes, resets and bans.</summary>
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

        var bans = await _groups.ListBansAsync(channel.ChannelId, cancellationToken);
        var devices = await _devices.ListDevicesAsync(allowed, includeRevoked: false, cancellationToken);
        return devices
            .Where(d => !bans.Any(b => b.UserId == d.UserId && (b.DeviceId is null || b.DeviceId == d.DeviceId)))
            .Select(d => new DeviceRef(d.UserId, d.DeviceId))
            .ToList();
    }

    private async Task<bool> BelongsAsync(ChannelRecord channel, DeviceRef device, CancellationToken cancellationToken)
    {
        var record = await _devices.FindDeviceAsync(device.UserId, device.DeviceId, cancellationToken);
        return record is { IsActive: true }
               && await _policy.IsAllowedAsync(device.UserId, channel, cancellationToken)
               && !await _groups.IsBannedAsync(channel.ChannelId, device.UserId, device.DeviceId, cancellationToken);
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
    /// <remarks>
    /// A device a channel manager banned from the group (<see cref="BanAsync"/>) is refused with
    /// <c>not_allowed_in_group</c>.
    /// </remarks>
    private async Task<ChannelRecord> RequireAccessAsync(UserSession session, Guid channelId, CancellationToken cancellationToken)
    {
        var channel = await RequireVisibleAsync(session, channelId, cancellationToken);
        if (await _groups.IsBannedAsync(channel.ChannelId, session.UserId, session.DeviceId, cancellationToken))
            throw ApiException.Forbidden("not_allowed_in_group", "This device is kept out of this channel's group.");

        return channel;
    }

    private async Task<ChannelRecord> RequireVisibleAsync(UserSession session, Guid channelId, CancellationToken cancellationToken)
    {
        await RequireDeviceAsync(session, cancellationToken);

        var channel = await _channels.FindByIdAsync(channelId, cancellationToken);
        if (channel is null || !await _policy.IsAllowedAsync(session.UserId, channel, cancellationToken))
            throw ApiException.NotFound("channel_not_found", "No channel with that id exists, or you cannot see it.");

        return channel;
    }

    /// <summary>
    /// The channel, for one of its managers: somebody who may delete it (<c>delete_channels</c>), and
    /// with it its group, can also reset that group and keep devices out of it. Not refused for a ban
    /// of their own, so a manager can always lift one.
    /// </summary>
    private async Task<ChannelRecord> RequireManagerAsync(UserSession session, Guid channelId, CancellationToken cancellationToken)
    {
        var channel = await RequireVisibleAsync(session, channelId, cancellationToken);
        (await _permissions.GetAsync(session.UserId, channel, cancellationToken))
            .Require(Permission.DeleteChannels, "manage this channel's encryption group");
        return channel;
    }

    private async Task<T> WithGroupLockAsync<T>(Guid channelId, Func<Task<T>> work, CancellationToken cancellationToken)
    {
        var gate = _groupLocks[(channelId.GetHashCode() & int.MaxValue) % _groupLocks.Length];
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await work();
        }
        finally
        {
            gate.Release();
        }
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

    private MlsEnvelope ParseGroupInfo(ChannelRecord channel, long generation, byte[]? groupInfo)
    {
        if (groupInfo is not { Length: > 0 } || groupInfo.Length > _options.MaximumMlsMessageBytes)
            throw ApiException.BadRequest("invalid_mls_message",
                $"A group info is 1 to {_options.MaximumMlsMessageBytes} bytes.");

        var envelope = MlsFraming.Parse(groupInfo);
        if (envelope.WireFormat != MlsWireFormat.GroupInfo)
            throw ApiException.BadRequest("invalid_mls_message", "That is not an MLS group info.");
        RequireGroupId(channel, generation, envelope.GroupId);
        return envelope;
    }

    private static void RequireGroupId(ChannelRecord channel, long generation, byte[]? groupId)
    {
        if (groupId is null || !groupId.AsSpan().SequenceEqual(MlsFraming.GroupIdFor(channel.ChannelId, generation)))
            throw ApiException.BadRequest("wrong_group",
                "That message is for a different channel's group, or another generation of this one's.");
    }

    /// <summary>
    /// Who is sent a group's traffic: everybody the policy lets in, except an account a manager has
    /// kept out of the group on every device. The bans are read once, when the event is published.
    /// </summary>
    private async Task<EventAudience> AudienceForAsync(ChannelRecord channel, CancellationToken cancellationToken)
    {
        var banned = (await _groups.ListBansAsync(channel.ChannelId, cancellationToken))
            .Where(b => b.DeviceId is null)
            .Select(b => b.UserId)
            .ToHashSet();
        return async (userId, token) => !banned.Contains(userId) && await _policy.IsAllowedAsync(userId, channel, token);
    }

    private static GroupStateResponse ToState(MlsGroupRecord group) =>
        new(group.ChannelId, group.Epoch, group.GroupInfo, group.GroupInfoEpoch, group.Generation);
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
