using Microsoft.Extensions.Hosting;

namespace RaindropServer.Attachments;

/// <summary>
/// Clears away what nobody can reach any more: uploads never sent in a message within
/// <see cref="AttachmentOptions.PendingUploadExpiry"/>, the files of deleted messages and channels, and
/// files in storage that no record points at (left by a crash between writing a file and recording it).
/// Runs at startup and every <see cref="AttachmentOptions.MaintenanceInterval"/>.
/// </summary>
public sealed class AttachmentMaintenanceService : BackgroundService
{
    private const int Batch = 200;

    private readonly IAttachmentStore _store;
    private readonly IAttachmentStorage _storage;
    private readonly IReadOnlyList<IAttachmentListener> _listeners;
    private readonly AttachmentOptions _options;
    private readonly TimeProvider _time;

    public AttachmentMaintenanceService(
        IAttachmentStore store,
        IAttachmentStorage storage,
        IEnumerable<IAttachmentListener> listeners,
        AttachmentOptions options,
        TimeProvider? time = null)
    {
        _store = store;
        _storage = storage;
        _listeners = listeners.ToList();
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.MaintenanceInterval, _time);
        var lastOrphanSweep = DateTime.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);

                // Listing every stored file is the expensive part, so it happens at startup and daily.
                var now = _time.GetUtcNow().UtcDateTime;
                if (now - lastOrphanSweep >= TimeSpan.FromDays(1))
                {
                    await RemoveOrphansAsync(stoppingToken);
                    lastOrphanSweep = now;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Storage or the database being briefly unreachable must not take the server down; the
                // next tick tries again.
                Console.Error.WriteLine($"[raindrop] attachment maintenance failed: {ex.Message}");
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

    /// <summary>
    /// Expires stale uploads and removes the files of every deleted attachment. Returns how many files
    /// were removed.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        await _store.ExpirePendingAsync(now - _options.PendingUploadExpiry, now, cancellationToken);

        var removed = 0;
        while (true)
        {
            var deleted = await _store.ListDeletedAsync(Batch, cancellationToken);

            foreach (var attachment in deleted)
            {
                await _storage.DeleteAsync(attachment.StorageKey, cancellationToken);
                await _store.RemoveAsync(attachment.AttachmentId, cancellationToken);
                removed++;

                foreach (var listener in _listeners)
                {
                    try
                    {
                        await listener.OnRemovedAsync(attachment, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Console.Error.WriteLine($"[raindrop] an attachment listener failed: {ex.Message}");
                    }
                }
            }

            if (deleted.Count < Batch)
                return removed;
        }
    }

    /// <summary>
    /// Removes stored files with no record behind them, once they are older than an upload is allowed
    /// to wait — so a file whose record is being written right now is never mistaken for one. Returns
    /// how many were removed.
    /// </summary>
    public async Task<int> RemoveOrphansAsync(CancellationToken cancellationToken)
    {
        var cutoff = _time.GetUtcNow().UtcDateTime - _options.PendingUploadExpiry;
        var removed = 0;

        await foreach (var (key, lastWritten) in _storage.ListAsync(cancellationToken))
        {
            if (lastWritten > cutoff || !Guid.TryParseExact(key, "N", out var id))
                continue;

            if (await _store.FindAsync(id, cancellationToken) is not null)
                continue;

            await _storage.DeleteAsync(key, cancellationToken);
            removed++;
        }

        return removed;
    }
}
