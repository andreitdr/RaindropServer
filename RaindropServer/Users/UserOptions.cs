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
}
