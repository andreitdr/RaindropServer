using Microsoft.Extensions.Hosting;

namespace RaindropServer.Users;

/// <summary>
/// Enforces the retention limit on users who left with their data preserved: once the window closes
/// their username and password are erased, leaving only the UUID tombstone their messages point at.
/// Runs on a timer for as long as the server is up, and sweeps once at startup so a server that was
/// offline past an expiry date still cleans up promptly.
/// </summary>
public sealed class RetentionCleanupService : BackgroundService
{
    private readonly IUserStore _store;
    private readonly UserOptions _options;
    private readonly TimeProvider _time;

    public RetentionCleanupService(IUserStore store, UserOptions options, TimeProvider? time = null)
    {
        _store = store;
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.RetentionSweepInterval, _time);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A sweep failure (say, the database is briefly unreachable) must not take the
                // server down — the next tick will try again.
                Console.Error.WriteLine($"[raindrop] retention sweep failed: {ex.Message}");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Erases every preserved account whose retention window has closed. Returns how many were erased.</summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var expired = await _store.FindExpiredPreservedAsync(now, cancellationToken);

        foreach (var user in expired)
            await _store.UpdateAsync(user.AsErased(now), cancellationToken);

        return expired.Count;
    }
}
