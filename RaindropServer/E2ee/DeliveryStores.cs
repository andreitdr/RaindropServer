namespace RaindropServer.E2ee;

/// <summary>A channel's MLS group, as the delivery service tracks it. The server is not a member.</summary>
/// <param name="ChannelId">The channel; the group id is <see cref="MlsFraming.GroupIdFor"/> of it.</param>
/// <param name="Epoch">The group's current epoch: the one the next commit must be made in.</param>
/// <param name="GroupInfo">The latest signed group info a member published, for devices joining by themselves.</param>
/// <param name="GroupInfoEpoch">The epoch <paramref name="GroupInfo"/> describes.</param>
/// <param name="UpdatedAtUtc">When a commit or group info last arrived.</param>
public sealed record MlsGroupRecord(
    Guid ChannelId,
    long Epoch,
    byte[]? GroupInfo,
    long GroupInfoEpoch,
    DateTime UpdatedAtUtc);

/// <summary>One accepted commit: the only one for its epoch.</summary>
/// <param name="Epoch">The epoch it was made in; merging it moves the group to <c>Epoch + 1</c>.</param>
public sealed record HandshakeRecord(
    Guid ChannelId,
    long Epoch,
    byte[] Message,
    Guid SenderUserId,
    DateTime CreatedAtUtc);

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

    /// <summary>Forgets the group, its log and its pending welcomes (the channel was deleted).</summary>
    Task DeleteAsync(Guid channelId, CancellationToken cancellationToken);
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
