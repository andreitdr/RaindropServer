namespace RaindropServer.E2ee;

/// <summary>A channel's MLS group, as the delivery service tracks it. The server is not a member.</summary>
/// <param name="ChannelId">The channel; the group id is <see cref="MlsFraming.GroupIdFor(Guid, long)"/> of it and <paramref name="Generation"/>.</param>
/// <param name="Epoch">The group's current epoch: the one the next commit must be made in.</param>
/// <param name="GroupInfo">The latest signed group info a member published, for devices joining by themselves.</param>
/// <param name="GroupInfoEpoch">The epoch <paramref name="GroupInfo"/> describes.</param>
/// <param name="UpdatedAtUtc">When a commit or group info last arrived.</param>
/// <param name="Generation">
/// Which generation of the channel's group this is: 0 until a reset (<see cref="MlsGroupResetRecord"/>)
/// started a new one. Read from the resets, not stored with the group.
/// </param>
public sealed record MlsGroupRecord(
    Guid ChannelId,
    long Epoch,
    byte[]? GroupInfo,
    long GroupInfoEpoch,
    DateTime UpdatedAtUtc,
    long Generation = 0);

/// <summary>One accepted commit: the only one for its epoch.</summary>
/// <param name="Epoch">The epoch it was made in; merging it moves the group to <c>Epoch + 1</c>.</param>
/// <param name="SenderDeviceId">
/// The device whose bound session submitted it, so a commit that breaks the group can be attributed.
/// Null for commits stored before this was recorded.
/// </param>
public sealed record HandshakeRecord(
    Guid ChannelId,
    long Epoch,
    byte[] Message,
    Guid SenderUserId,
    DateTime CreatedAtUtc,
    Guid? SenderDeviceId = null);

/// <summary>What a member reported it could not get past when it reset a channel's group.</summary>
public static class GroupResetReports
{
    /// <summary>A commit in the group's log.</summary>
    public const string Commit = "commit";

    /// <summary>The group info published for joining by external commit.</summary>
    public const string GroupInfo = "group_info";
}

/// <summary>
/// One reset of a channel's group: the start of <paramref name="Generation"/>. The previous
/// generation's group, log and Welcomes are gone; this row is what remains of it, and it is how a
/// broken commit is attributed.
/// </summary>
/// <param name="Generation">The generation this reset started (at least 1).</param>
/// <param name="RequestedByUserId">Who asked for it.</param>
/// <param name="RequestedByDeviceId">From which device.</param>
/// <param name="ReportedKind">
/// What they reported (<see cref="GroupResetReports"/>), or null for a channel manager's reset without
/// a report.
/// </param>
/// <param name="ReportedEpoch">The epoch of the reported commit or group info.</param>
/// <param name="ReportedSha256">SHA-256 of the reported commit or group info.</param>
/// <param name="SenderUserId">For a reported commit: the account whose session submitted it.</param>
/// <param name="SenderDeviceId">For a reported commit: the device it was submitted from, when recorded.</param>
public sealed record MlsGroupResetRecord(
    Guid ChannelId,
    long Generation,
    DateTime ResetAtUtc,
    Guid RequestedByUserId,
    Guid RequestedByDeviceId,
    string? ReportedKind,
    long? ReportedEpoch,
    byte[]? ReportedSha256,
    Guid? SenderUserId,
    Guid? SenderDeviceId);

/// <summary>
/// A device, or every device of an account, that a channel's managers keep out of its group.
/// </summary>
/// <param name="DeviceId">The device, or null for every device of <paramref name="UserId"/>.</param>
/// <param name="BannedBy">The manager who set it.</param>
public sealed record MlsGroupBanRecord(
    Guid ChannelId,
    Guid UserId,
    Guid? DeviceId,
    Guid BannedBy,
    DateTime BannedAtUtc);

/// <summary>A key package a device uploaded, so others can add it to groups while it is offline.</summary>
/// <param name="LastResort">The reusable one handed out when the one-time ones run out; never consumed.</param>
public sealed record KeyPackageRecord(
    Guid PackageId,
    Guid UserId,
    Guid DeviceId,
    byte[] KeyPackage,
    bool LastResort,
    DateTime CreatedAtUtc);

/// <summary>A Welcome waiting for the device a commit added.</summary>
public sealed record WelcomeRecord(
    Guid WelcomeId,
    Guid UserId,
    Guid DeviceId,
    Guid ChannelId,
    long Epoch,
    byte[] Welcome,
    DateTime CreatedAtUtc);

/// <summary>Persistence for groups and their commit logs.</summary>
public interface IMlsGroupStore
{
    Task<MlsGroupRecord?> FindAsync(Guid channelId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> ListChannelIdsAsync(CancellationToken cancellationToken);

    /// <summary>Records a new group. Returns false when the channel already has one.</summary>
    Task<bool> TryCreateAsync(MlsGroupRecord group, CancellationToken cancellationToken);

    /// <summary>
    /// Accepts <paramref name="commit"/> if the group is still at its epoch, advancing the group by
    /// one, atomically. Returns false, changing nothing, when another commit already took that epoch:
    /// that is how the server keeps a group from forking.
    /// </summary>
    Task<bool> TryAppendCommitAsync(HandshakeRecord commit, CancellationToken cancellationToken);

    /// <summary>Stores a group info for <paramref name="epoch"/>, if that is still the group's epoch.</summary>
    Task<bool> PutGroupInfoAsync(Guid channelId, long epoch, byte[] groupInfo, DateTime atUtc, CancellationToken cancellationToken);

    /// <summary>Commits made in <paramref name="fromEpoch"/> or later, oldest first.</summary>
    Task<IReadOnlyList<HandshakeRecord>> ReadLogAsync(Guid channelId, long fromEpoch, int limit, CancellationToken cancellationToken);

    /// <summary>The oldest epoch still in the log, or null when it is empty.</summary>
    Task<long?> OldestLogEpochAsync(Guid channelId, CancellationToken cancellationToken);

    /// <summary>Forgets commits made before <paramref name="epoch"/>.</summary>
    Task PruneLogAsync(Guid channelId, long epoch, CancellationToken cancellationToken);

    /// <summary>
    /// Forgets the group, its log, its pending welcomes, its resets and its bans (the channel was
    /// deleted).
    /// </summary>
    Task DeleteAsync(Guid channelId, CancellationToken cancellationToken);

    // ── Generations ──────────────────────────────────────────────────────────

    /// <summary>The channel group's current generation: the latest reset's, or 0 when it was never reset.</summary>
    Task<long> GenerationAsync(Guid channelId, CancellationToken cancellationToken);

    /// <summary>
    /// Starts <see cref="MlsGroupResetRecord.Generation"/>, atomically: records the reset and forgets
    /// the previous generation's group, log and pending Welcomes. Returns false, changing nothing, when
    /// that generation was already started — the reset row's key refuses a second one, as the log's
    /// refuses a second commit for an epoch.
    /// </summary>
    Task<bool> TryResetAsync(MlsGroupResetRecord reset, CancellationToken cancellationToken);

    /// <summary>The channel group's resets, newest first.</summary>
    Task<IReadOnlyList<MlsGroupResetRecord>> ListResetsAsync(Guid channelId, int limit, CancellationToken cancellationToken);

    /// <summary>The latest reset <paramref name="userId"/> asked for in the channel, or null.</summary>
    Task<MlsGroupResetRecord?> LatestResetByAsync(Guid channelId, Guid userId, CancellationToken cancellationToken);

    // ── Bans ─────────────────────────────────────────────────────────────────

    /// <summary>Adds a ban. Returns false when the same one is already there.</summary>
    Task<bool> TryBanAsync(MlsGroupBanRecord ban, CancellationToken cancellationToken);

    /// <summary>Lifts a ban (<paramref name="deviceId"/> null: the one on every device). Returns whether there was one.</summary>
    Task<bool> UnbanAsync(Guid channelId, Guid userId, Guid? deviceId, CancellationToken cancellationToken);

    Task<IReadOnlyList<MlsGroupBanRecord>> ListBansAsync(Guid channelId, CancellationToken cancellationToken);

    /// <summary>
    /// Whether <paramref name="userId"/> is kept out of the channel's group: on every device, or — when
    /// <paramref name="deviceId"/> is given — on that one.
    /// </summary>
    Task<bool> IsBannedAsync(Guid channelId, Guid userId, Guid? deviceId, CancellationToken cancellationToken);
}

/// <summary>Persistence for devices' key packages.</summary>
public interface IKeyPackageStore
{
    Task AddAsync(IReadOnlyList<KeyPackageRecord> packages, CancellationToken cancellationToken);

    /// <summary>How many one-time packages a device has left.</summary>
    Task<int> CountOneTimeAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken);

    /// <summary>Replaces a device's last-resort package.</summary>
    Task ReplaceLastResortAsync(KeyPackageRecord package, CancellationToken cancellationToken);

    /// <summary>
    /// Hands out one package for a device: the oldest one-time package, removed so nobody else gets
    /// it, or failing that the last-resort one, which stays. Null when the device has none.
    /// </summary>
    Task<KeyPackageRecord?> ClaimAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken);

    Task DeleteForDeviceAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken);
}

/// <summary>Persistence for Welcomes waiting for their devices.</summary>
public interface IWelcomeStore
{
    Task AddAsync(IReadOnlyList<WelcomeRecord> welcomes, CancellationToken cancellationToken);

    Task<IReadOnlyList<WelcomeRecord>> ListAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken);

    Task DeleteAsync(Guid userId, Guid deviceId, IReadOnlyCollection<Guid> welcomeIds, CancellationToken cancellationToken);

    Task DeleteForDeviceAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken);
}
