using Microsoft.Extensions.Hosting;
using RaindropServer.Dashboard.Metrics;
using RaindropServer.Events;

namespace RaindropServer.Dashboard;

/// <summary>One point on the dashboard's charts.</summary>
/// <param name="AtUtc">When it was taken.</param>
/// <param name="ProcessCpuPercent">The server process's CPU, as a share of every core.</param>
/// <param name="ProcessMemoryBytes">The server process's working set.</param>
/// <param name="SystemCpuPercent">The machine's CPU — or, in a container, the container's against its limit.</param>
/// <param name="SystemMemoryUsedBytes">The machine's memory in use — or the container's.</param>
/// <param name="EventStreams">Open event streams: connected clients.</param>
/// <param name="MembersOnline">Members with at least one open stream.</param>
public sealed record DashboardSample(
    DateTime AtUtc,
    double? ProcessCpuPercent,
    long ProcessMemoryBytes,
    double? SystemCpuPercent,
    long? SystemMemoryUsedBytes,
    int EventStreams,
    int MembersOnline);

/// <summary>
/// Where the server runs: the machine, or the container it is in. Decided once, at start.
/// </summary>
public sealed class DashboardEnvironment
{
    public DashboardEnvironment(string? container = null, HostMetrics? host = null)
    {
        Runtime = container;
        if (container is null)
            Host = host ?? HostMetrics.ForThisPlatform();
        else
            Container = new ContainerMetrics(container);
    }

    /// <summary>The environment as detected on this machine.</summary>
    public static DashboardEnvironment Detect() => new(ContainerDetector.Detect());

    /// <summary><see cref="ContainerDetector.Docker"/>, another container runtime, or null for a machine of its own.</summary>
    public string? Runtime { get; }

    /// <summary>The machine's readers, when not in a container.</summary>
    public HostMetrics? Host { get; }

    /// <summary>The container's readers, when in one.</summary>
    public ContainerMetrics? Container { get; }
}

/// <summary>
/// Samples every five seconds — CPU can only be measured between two readings anyway — and keeps the
/// last hour for the dashboard's charts. In memory: a restart starts the charts again.
/// </summary>
public sealed class DashboardSampler : BackgroundService
{
    /// <summary>How often a sample is taken.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private const int Capacity = 720; // an hour of samples

    private readonly ProcessMetrics _process;
    private readonly DashboardEnvironment _environment;
    private readonly IEventConnectionRegistry _connections;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Queue<DashboardSample> _samples = new();
    private double? _systemCpuPercent;

    public DashboardSampler(
        ProcessMetrics process, DashboardEnvironment environment, IEventConnectionRegistry connections, TimeProvider? time = null)
    {
        _process = process;
        _environment = environment;
        _connections = connections;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The machine's (or container's) CPU at the last sample.</summary>
    public double? SystemCpuPercent
    {
        get
        {
            lock (_gate)
                return _systemCpuPercent;
        }
    }

    /// <summary>The samples of the last hour, oldest first.</summary>
    public IReadOnlyList<DashboardSample> History
    {
        get
        {
            lock (_gate)
                return _samples.ToList();
        }
    }

    /// <summary>Takes one sample now.</summary>
    public DashboardSample Sample()
    {
        var processCpu = _process.SampleCpuPercent();
        var process = _process.Read();

        double? systemCpu;
        long? systemMemory;

        if (_environment.Container is { } container)
        {
            container.SampleCpuCores();
            var status = container.Read();
            systemCpu = status.CpuPercent;
            systemMemory = status.MemoryUsedBytes;
        }
        else
        {
            systemCpu = _environment.Host!.SampleCpuPercent();
            var status = _environment.Host.Read(systemCpu);
            systemMemory = status.MemoryTotalBytes - status.MemoryAvailableBytes;
        }

        var sample = new DashboardSample(
            _time.GetUtcNow().UtcDateTime,
            processCpu,
            process.WorkingSetBytes,
            systemCpu,
            systemMemory,
            _connections.Count,
            _connections.ConnectedUserIds().Count);

        lock (_gate)
        {
            _systemCpuPercent = systemCpu;
            _samples.Enqueue(sample);
            while (_samples.Count > Capacity)
                _samples.Dequeue();
        }

        return sample;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, _time);

        try
        {
            do
            {
                try
                {
                    Sample();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    // A reading that fails this time (a file that vanished, a process that could not be
                    // asked) leaves a gap in the chart rather than stopping the charts altogether.
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
