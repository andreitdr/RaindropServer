namespace RaindropServer.E2ee;

/// <summary>
/// Server code that reacts when devices come and go. The MLS delivery service uses it to take a
/// revoked device out of every group, and a plugin can use it for auditing. Called by
/// <see cref="DeviceService"/> after the change is written. A listener that throws is reported and
/// stepped over, because the change has already happened.
/// </summary>
public interface IDeviceListener
{
    Task OnDeviceAddedAsync(DeviceRecord device, CancellationToken cancellationToken) => Task.CompletedTask;

    Task OnDeviceRevokedAsync(DeviceRecord device, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The user's identity key is gone and every device of theirs revoked.</summary>
    Task OnIdentityResetAsync(Guid userId, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>End-to-end encryption limits.</summary>
public sealed class E2eeOptions
{
    /// <summary>The most active devices one user may have.</summary>
    public int MaximumDevicesPerUser { get; set; } = 10;

    /// <summary>How long a session-binding challenge may be answered.</summary>
    public TimeSpan ChallengeLifetime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long a device link waits for an existing device to approve it.</summary>
    public TimeSpan DeviceLinkLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The largest sealed key backup accepted.</summary>
    public int MaximumBackupBytes { get; set; } = 64 * 1024;

    /// <summary>The most users one device listing may ask about.</summary>
    public int MaximumUsersPerListing { get; set; } = 500;

    // ── The MLS delivery service ──

    /// <summary>The largest commit, Welcome, group info or key package accepted.</summary>
    public int MaximumMlsMessageBytes { get; set; } = 256 * 1024;

    /// <summary>How many one-time key packages one device may keep on the server.</summary>
    public int MaximumKeyPackagesPerDevice { get; set; } = 200;

    /// <summary>How many key packages one upload may carry.</summary>
    public int MaximumKeyPackagesPerUpload { get; set; } = 100;

    /// <summary>Below this many one-time key packages, a device's owner is told to upload more.</summary>
    public int KeyPackageLowWatermark { get; set; } = 10;

    /// <summary>The most devices one commit may welcome, or one claim may ask for.</summary>
    public int MaximumWelcomeRecipients { get; set; } = 100;

    /// <summary>
    /// How many epochs of commits a group's log keeps. A device further behind rejoins by external
    /// commit (and misses the messages in between, as forward-only history means it would anyway).
    /// </summary>
    public int MaximumLogEpochs { get; set; } = 1000;

    /// <summary>The most commits one sync returns; a device further behind asks again.</summary>
    public int SyncPageSize { get; set; } = 500;
}
