using RaindropServer.Configuration;

namespace RaindropServer.Users;

/// <summary>Server-owner-tunable rules for user accounts, sessions and data retention.</summary>
public sealed class UserOptions
{
    /// <summary>
    /// How long a preserved account is kept after the user leaves the server. Capped at two years
    /// to stay inside the data-retention limit; a shorter window is allowed, a longer one is not.
    /// </summary>
    public TimeSpan DataRetentionAfterLeaving
    {
        get => _dataRetentionAfterLeaving;
        set
        {
            if (value <= TimeSpan.Zero || value > MaximumDataRetention)
                throw new ArgumentOutOfRangeException(nameof(value),
                    $"Preserved data may be kept for at most {MaximumDataRetention.TotalDays:N0} days.");

            _dataRetentionAfterLeaving = value;
        }
    }

    private TimeSpan _dataRetentionAfterLeaving = MaximumDataRetention;

    /// <summary>Two years — the legal ceiling on keeping a departed user's personal data.</summary>
    public static readonly TimeSpan MaximumDataRetention = TimeSpan.FromDays(730);

    /// <summary>
    /// How long a session survives without any request. Clients are expected to log out when they
    /// close, so this is only a backstop for clients that vanish.
    /// </summary>
    public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromHours(12);

    /// <summary>How often expired preserved accounts are swept and erased.</summary>
    public TimeSpan RetentionSweepInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>The shortest password this server accepts.</summary>
    public int MinimumPasswordLength { get; set; } = 8;

    /// <summary>
    /// The longest password this server accepts. A cap keeps an absurdly long password from being
    /// pushed through the hash function.
    /// </summary>
    public int MaximumPasswordLength { get; set; } = 256;

    /// <summary>
    /// Reports account rules that contradict each other or would surprise the owner who set them.
    /// The retention window polices itself in <see cref="DataRetentionAfterLeaving"/>, which refuses
    /// a value over the legal ceiling outright.
    /// </summary>
    public void Validate(ConfigurationReport report)
    {
        report.Range("sessionIdleTimeoutHours", SessionIdleTimeout, TimeSpan.FromMinutes(1), TimeSpan.FromDays(90));

        if (SessionIdleTimeout > TimeSpan.FromDays(30))
            report.Warn("sessionIdleTimeoutHours",
                $"is {SessionIdleTimeout.TotalDays:0.##} days. A session token stays usable that long after its device " +
                "was last seen, which is a long time for a device that was lost rather than closed.");
        else if (SessionIdleTimeout < TimeSpan.FromMinutes(15))
            report.Warn("sessionIdleTimeoutHours",
                $"is {SessionIdleTimeout.TotalMinutes:0.##} minutes, short enough to log a member out while they are " +
                "reading rather than typing.");

        if (MinimumPasswordLength < 1)
            report.Error("minimumPasswordLength", "must be at least 1: a server cannot accept an empty password.");
        else if (MinimumPasswordLength < 8)
            report.Warn("minimumPasswordLength",
                $"is {MinimumPasswordLength}. Eight characters is the shortest most guidance considers a password at all.");

        if (MaximumPasswordLength < MinimumPasswordLength)
            report.Error("minimumPasswordLength",
                $"is {MinimumPasswordLength}, longer than the {MaximumPasswordLength}-character maximum, so no password " +
                "this server accepts exists.");

        if (RetentionSweepInterval <= TimeSpan.Zero)
            report.Error("retentionSweepInterval", "must be positive, or expired accounts are never erased.");
        else if (RetentionSweepInterval > TimeSpan.FromDays(1))
            report.Warn("retentionSweepInterval",
                $"is {RetentionSweepInterval.TotalHours:0.##} hours, so a departed user's data can outlive its retention " +
                "window by nearly that long.");
    }
}
