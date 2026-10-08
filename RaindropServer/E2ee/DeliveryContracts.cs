namespace RaindropServer.E2ee;

// The MLS delivery service's contracts. Every MLS message is opaque bytes, protobuf `bytes` on the
// wire. Groups are named by their channel; the MLS group id is the channel's UUID
// as 16 big-endian bytes.

/// <param name="UserId">The device's owner.</param>
/// <param name="DeviceId">The device.</param>
public record DeviceRef(Guid UserId, Guid DeviceId);

// ── Groups ───────────────────────────────────────────────────────────────────

/// <param name="ChannelId">The channel whose group this is.</param>
public record GroupRequest(Guid ChannelId);

/// <param name="ChannelId">The channel whose group to start.</param>
/// <param name="GroupInfo">The new group's signed group info (MLS <c>GroupInfo</c> message).</param>
public record CreateGroupRequest(Guid ChannelId, byte[] GroupInfo);

/// <param name="ChannelId">The channel.</param>
/// <param name="Epoch">The epoch the next commit must be made in.</param>
/// <param name="GroupInfo">The latest published group info, for joining by external commit.</param>
/// <param name="GroupInfoEpoch">The epoch <paramref name="GroupInfo"/> describes; behind <paramref name="Epoch"/> until a member publishes a newer one.</param>
public record GroupStateResponse(Guid ChannelId, long Epoch, byte[]? GroupInfo, long GroupInfoEpoch);

/// <param name="ChannelId">The channel.</param>
/// <param name="Commit">The MLS commit, made in the group's current epoch.</param>
/// <param name="Welcome">For the devices the commit adds, if any.</param>
/// <param name="WelcomeRecipients">Which devices those are — the server cannot tell from the Welcome.</param>
public record SubmitCommitRequest(
    Guid ChannelId,
    byte[] Commit,
    byte[]? Welcome,
    IReadOnlyList<DeviceRef>? WelcomeRecipients);

/// <param name="Epoch">The group's epoch now that the commit is accepted.</param>
public record SubmitCommitResponse(long Epoch);

/// <param name="ChannelId">The channel.</param>
/// <param name="GroupInfo">A group info for the group's current epoch.</param>
public record PutGroupInfoRequest(Guid ChannelId, byte[] GroupInfo);

/// <param name="ChannelId">The channel.</param>
/// <param name="FromEpoch">The epoch this device is in: commits made in it and later are returned.</param>
public record SyncGroupRequest(Guid ChannelId, long FromEpoch);

/// <param name="Epoch">One commit, made in this epoch.</param>
/// <param name="Message">The MLS commit.</param>
public record HandshakeSummary(long Epoch, byte[] Message);

/// <param name="Epoch">The group's current epoch.</param>
/// <param name="Commits">Commits from the requested epoch on, oldest first — possibly one page of many.</param>
/// <param name="ResyncRequired">
/// True when commits this device needs have been pruned: it must rejoin by external commit, and loses
/// the messages in between, as forward-only history means it would anyway.
/// </param>
public record SyncGroupResponse(long Epoch, IReadOnlyList<HandshakeSummary> Commits, bool ResyncRequired);

/// <param name="ChannelId">The channel.</param>
/// <param name="Devices">Every device that should be in the channel's group right now.</param>
public record GroupRosterResponse(Guid ChannelId, IReadOnlyList<DeviceRef> Devices);

// ── Key packages ─────────────────────────────────────────────────────────────

/// <param name="KeyPackages">One-time key packages for this session's device.</param>
/// <param name="LastResort">A reusable key package to hand out when the one-time ones run out.</param>
public record UploadKeyPackagesRequest(IReadOnlyList<byte[]>? KeyPackages, byte[]? LastResort);

/// <param name="OneTime">How many one-time packages this device has on the server.</param>
public record KeyPackageCountResponse(int OneTime);

/// <param name="ChannelId">The channel whose group the devices are being added to.</param>
/// <param name="Devices">The devices to add.</param>
public record ClaimKeyPackagesRequest(Guid ChannelId, IReadOnlyList<DeviceRef> Devices);

public record ClaimedKeyPackage(Guid UserId, Guid DeviceId, byte[] KeyPackage);

/// <param name="KeyPackages">One per device that had one; devices with none are left out.</param>
public record ClaimKeyPackagesResponse(IReadOnlyList<ClaimedKeyPackage> KeyPackages);

// ── Welcomes ─────────────────────────────────────────────────────────────────

public record PendingWelcome(Guid WelcomeId, Guid ChannelId, long Epoch, byte[] Welcome);

public record WelcomesResponse(IReadOnlyList<PendingWelcome> Welcomes);

/// <param name="WelcomeIds">Welcomes this device has processed and no longer needs.</param>
public record AckWelcomesRequest(IReadOnlyList<Guid> WelcomeIds);

// ── Events ───────────────────────────────────────────────────────────────────

public static class DeliveryEventTypes
{
    /// <summary>A commit was accepted. Data: <see cref="HandshakeEvent"/>. To everyone allowed in the group.</summary>
    public const string Handshake = "e2ee.handshake";

    /// <summary>A Welcome is waiting for one of the recipient's devices. Data: <see cref="WelcomeEvent"/>.</summary>
    public const string Welcome = "e2ee.welcome";

    /// <summary>
    /// Who should be in a group may have changed: members compare the tree with
    /// <c>groups/roster</c> and commit the difference. Data: <see cref="GroupReconcileEvent"/>.
    /// </summary>
    public const string GroupReconcile = "e2ee.group.reconcile";

    /// <summary>A device is running out of one-time key packages. Data: <see cref="KeyPackagesLowEvent"/>. To its owner.</summary>
    public const string KeyPackagesLow = "e2ee.keypackages.low";
}

public record HandshakeEvent(Guid ChannelId, long Epoch, byte[] Message);

public record WelcomeEvent(Guid ChannelId, Guid DeviceId);

public record GroupReconcileEvent(Guid ChannelId);

public record KeyPackagesLowEvent(Guid DeviceId, int Remaining);
