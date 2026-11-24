using System.Diagnostics;

namespace RaindropServer.Dashboard.Metrics;

/// <summary>What the server process itself is using.</summary>
/// <param name="CpuPercent">CPU used since the last sample, as a share of every core on the machine.</param>
/// <param name="WorkingSetBytes">Physical memory the process holds.</param>
/// <param name="ManagedHeapBytes">Of that, the .NET heap.</param>
/// <param name="Threads">Operating-system threads.</param>
/// <param name="GarbageCollections">Collections of every generation since the process started.</param>
public sealed record ProcessStatus(double? CpuPercent, long WorkingSetBytes, long ManagedHeapBytes, int Threads, long GarbageCollections);

/// <summary>Reads <see cref="ProcessStatus"/>, measuring CPU between samples.</summary>
public sealed class ProcessMetrics
{
    private (TimeSpan Cpu, long AtTicks)? _previous;
    private double? _cpuPercent;

    /// <summary>Measures CPU since the previous call; null on the first.</summary>
    public double? SampleCpuPercent()
    {
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var now = Stopwatch.GetTimestamp();
        var previous = _previous;
        _previous = (cpu, now);

        if (previous is not { } before)
            return _cpuPercent = null;

        var elapsed = Stopwatch.GetElapsedTime(before.AtTicks, now);
        if (elapsed <= TimeSpan.Zero)
            return _cpuPercent;

        return _cpuPercent = Math.Clamp(100 * (cpu - before.Cpu) / (elapsed * Environment.ProcessorCount), 0, 100);
    }

    /// <summary>The process's figures, with the CPU use the last <see cref="SampleCpuPercent"/> measured.</summary>
    public ProcessStatus Read()
    {
        using var process = Process.GetCurrentProcess();

        long collections = 0;
        for (var generation = 0; generation <= GC.MaxGeneration; generation++)
            collections += GC.CollectionCount(generation);

        return new ProcessStatus(
            _cpuPercent,
            process.WorkingSet64,
            GC.GetTotalMemory(forceFullCollection: false),
            process.Threads.Count,
            collections);
    }
}
