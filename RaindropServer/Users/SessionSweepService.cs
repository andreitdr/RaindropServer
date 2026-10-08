using Microsoft.Extensions.Hosting;

namespace RaindropServer.Users;

/// <summary>
/// Forgets lapsed sessions every <see cref="Interval"/>. A session is only ever dropped otherwise when
/// its token is used again, so one whose client vanished without logging out would sit in memory until
/// the server restarted.
/// </summary>
public sealed class SessionSweepService : BackgroundService
{
    /// <summary>How often lapsed sessions are forgotten.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private readonly ISessionStore _sessions;
    private readonly TimeProvider _time;

    public SessionSweepService(ISessionStore sessions, TimeProvider? time = null)
    {
        _sessions = sessions;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, _time);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                _sessions.RemoveExpired();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
