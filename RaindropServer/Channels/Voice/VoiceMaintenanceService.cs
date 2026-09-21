using Microsoft.Extensions.Hosting;

namespace RaindropServer.Channels.Voice;

/// <summary>
/// The one background worker behind voice calls: ends leftover calls at startup, runs queued work
/// (<see cref="VoiceWorkQueue"/>) as it arrives, reconciles against the media server every
/// <see cref="VoiceOptions.ReconcileInterval"/>, and ends every call on the way down.
/// </summary>
/// <remarks>
/// One loop for all of it, so queued work and reconciliation never run concurrently and never have to
/// be made safe against each other. Every step is stepped over on failure — the media server being
/// briefly unreachable is the normal case this exists to recover from, not a reason to stop.
/// </remarks>
public sealed class VoiceMaintenanceService : BackgroundService
{
    /// <summary>How long ending every call may hold up shutdown, well inside the host's own budget.</summary>
    private static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(3);

    private readonly VoiceService _voice;
    private readonly VoiceWorkQueue _work;
    private readonly VoiceOptions _options;
    private readonly TimeProvider _time;

    /// <summary>The last failure reported, so a failure that repeats every tick is reported once.</summary>
    private string? _lastFailure;

    public VoiceMaintenanceService(VoiceService voice, VoiceWorkQueue work, VoiceOptions options, TimeProvider? time = null)
    {
        _voice = voice;
        _work = work;
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Every session died with the previous process, so no call it left running can be adopted.
        await RunAsync(_voice.EndAllCallsAsync, "ending leftover calls", stoppingToken);

        var nextReconcile = _time.GetUtcNow() + _options.ReconcileInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = nextReconcile - _time.GetUtcNow();

            if (wait > TimeSpan.Zero)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(wait);

                try
                {
                    await _work.Reader.WaitToReadAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // The wait ran out: time to reconcile.
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            while (_work.Reader.TryRead(out var item))
                await RunAsync(item, "queued voice work", stoppingToken);

            if (_time.GetUtcNow() >= nextReconcile)
            {
                await RunAsync(_voice.ReconcileAsync, "reconciling calls", stoppingToken);
                nextReconcile = _time.GetUtcNow() + _options.ReconcileInterval;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(ShutdownBudget);
        await RunAsync(_voice.EndAllCallsAsync, "ending calls on shutdown", budget.Token);
    }

    /// <summary>
    /// Runs one step, reporting a failure once rather than on every tick it keeps failing — a media
    /// server that is down stays down for a while — and reporting when it recovers.
    /// </summary>
    private async Task RunAsync(Func<CancellationToken, Task> step, string what, CancellationToken cancellationToken)
    {
        try
        {
            await step(cancellationToken);

            if (_lastFailure is not null)
            {
                Console.WriteLine("[raindrop] voice: the media server is answering again.");
                _lastFailure = null;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (ex.Message != _lastFailure)
                Console.Error.WriteLine($"[raindrop] voice: {what} failed: {ex.Message}");

            _lastFailure = ex.Message;
        }
    }
}
