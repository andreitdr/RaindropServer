using System.Collections.Concurrent;
using RaindropServer.E2ee;

namespace RaindropServer.Security;

/// <summary>
/// Limits how often one caller may make the calls that need no session: asking for a sign-in
/// challenge, answering one, fetching a sealed root, opening a pairing. Without it those are free
/// guesses and free memory for anybody who can reach the port.
/// </summary>
/// <remarks>
/// A fixed one-minute window per caller, held in memory and forgotten wholesale when the minute
/// turns — coarse, but it cannot grow past one minute's worth of callers, and a Raindrop server is a
/// single process. The transport decides what a caller is; the gRPC layer passes the remote address.
/// </remarks>
public sealed class RequestThrottle
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private const int MaximumCallers = 100_000;

    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly Func<int> _limit;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private DateTime _windowStartedUtc;

    public RequestThrottle(E2eeOptions options, TimeProvider? time = null)
        : this(() => options.AnonymousRequestsPerMinute, time)
    {
    }

    private RequestThrottle(Func<int> limit, TimeProvider? time)
    {
        _limit = limit;
        _time = time ?? TimeProvider.System;
        _windowStartedUtc = _time.GetUtcNow().UtcDateTime;
    }

    /// <summary>
    /// A throttle with a limit of its own rather than the anonymous calls' one — the owner's dashboard
    /// allows far fewer sign-in attempts than a client makes handshakes.
    /// </summary>
    public static RequestThrottle PerMinute(int requestsPerMinute, TimeProvider? time = null) =>
        new(() => requestsPerMinute, time);

    /// <summary>Counts one call by <paramref name="caller"/>, refusing it with <c>rate_limited</c> when it is one too many.</summary>
    public void Check(string caller)
    {
        var limit = _limit();
        if (limit <= 0)
            return;

        var now = _time.GetUtcNow().UtcDateTime;
        lock (_gate)
        {
            if (now - _windowStartedUtc >= Window || _counts.Count > MaximumCallers)
            {
                _counts.Clear();
                _windowStartedUtc = now;
            }
        }

        if (_counts.AddOrUpdate(caller, 1, (_, count) => count + 1) > limit)
        {
            // RESOURCE_EXHAUSTED is the status a client backs off on; the kind is named for its
            // other use, a payload over a size limit.
            throw ApiException.TooLarge("rate_limited", "Too many requests from this address. Wait a minute and try again.");
        }
    }
}
