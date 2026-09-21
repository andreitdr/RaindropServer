using System.Threading.Channels;

namespace RaindropServer.Channels.Voice;

/// <summary>
/// Voice work that must not run on the request, or the event publish, that caused it: re-checking
/// every participant after a role change, and telling the media server about a decision already made.
/// Drained one item at a time by <see cref="VoiceMaintenanceService"/>, which also runs the reconciler —
/// so the two never race each other.
/// </summary>
/// <remarks>
/// In-process event handlers run inside <c>EventHub.PublishAsync</c>, and session listeners inside the
/// request that logged somebody out; a round trip to the media server there would hold both up. Work
/// items are idempotent (they read the current state rather than carrying a decision), so running one
/// late, or twice, is harmless.
/// </remarks>
public sealed class VoiceWorkQueue
{
    private readonly Channel<Func<CancellationToken, Task>> _work =
        Channel.CreateUnbounded<Func<CancellationToken, Task>>(new UnboundedChannelOptions { SingleReader = true });

    private int _reevaluationPending;

    /// <summary>Queues a piece of work. Never blocks and never fails.</summary>
    public void Enqueue(Func<CancellationToken, Task> work) => _work.Writer.TryWrite(work);

    /// <summary>
    /// Queues one re-check of every participant, unless one is already waiting — a burst of role
    /// changes costs one pass, not one per change.
    /// </summary>
    public void EnqueueReevaluation(Func<CancellationToken, Task> reevaluate)
    {
        if (Interlocked.Exchange(ref _reevaluationPending, 1) == 1)
            return;

        Enqueue(async cancellationToken =>
        {
            Interlocked.Exchange(ref _reevaluationPending, 0);
            await reevaluate(cancellationToken);
        });
    }

    internal ChannelReader<Func<CancellationToken, Task>> Reader => _work.Reader;
}
