using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RaindropServer.Dashboard.Metrics;

/// <summary>
/// The container the server runs in, as the dashboard shows it instead of the machine: a container's
/// own limits and usage are what its owner can act on, and what it sees of the host is not its own.
/// </summary>
public sealed record ContainerStatus(
    string Runtime,
    string? ContainerId,
    string Hostname,
    string? ImageOperatingSystem,
    double? UptimeSeconds,
    double? CpuLimitCores,
    double? CpuCoresUsed,
    double? CpuPercent,
    long? MemoryUsedBytes,
    long? MemoryLimitBytes,
    long? Processes,
    long? ProcessLimit,
    long? NetworkReceivedBytes,
    long? NetworkSentBytes);

/// <summary>Whether this process runs in a container, and which kind.</summary>
public static partial class ContainerDetector
{
    /// <summary>Docker, which the dashboard names as such.</summary>
    public const string Docker = "docker";

    /// <summary>Some other container runtime — Podman, Kubernetes — that .NET's own images announce.</summary>
    public const string Other = "container";

    /// <summary>
    /// <see cref="Docker"/> when <c>/.dockerenv</c> exists (Docker makes it in every container) or this
    /// process's cgroup names Docker; the mount table is not asked, because a machine that merely runs
    /// Docker has <c>/var/lib/docker</c> mounts in its own; <see cref="Other"/> when only .NET's container images say so
    /// (<c>DOTNET_RUNNING_IN_CONTAINER</c>); null on a machine of its own.
    /// </summary>
    public static string? Detect(string root = "/", Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;

        if (!OperatingSystem.IsLinux() && root == "/")
            return null;

        if (File.Exists(Path.Combine(root, ".dockerenv")))
            return Docker;

        // cgroup v1 ("/docker/<id>") and the systemd driver ("docker-<id>.scope"); v2 shows "0::/".
        if (LinuxProc.ReadText(Path.Combine(root, "proc", "self", "cgroup")) is { } cgroup && DockerId().IsMatch(cgroup))
            return Docker;

        return string.Equals(environment("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)
            ? Other
            : null;
    }

    /// <summary>
    /// The container's id, as Docker names it: from the mounts it gives every container
    /// (<c>/var/lib/docker/containers/&lt;id&gt;/hostname</c>), or a cgroup v1 path.
    /// </summary>
    public static string? ContainerId(string root = "/")
    {
        var proc = Path.Combine(root, "proc", "self");

        // The line that mounts the container's own /etc/hostname, from Docker's folder for it.
        var hostname = LinuxProc.ReadLines(Path.Combine(proc, "mountinfo"))?
            .FirstOrDefault(line => line.Contains(" /etc/hostname ", StringComparison.Ordinal));

        foreach (var text in new[] { hostname, LinuxProc.ReadText(Path.Combine(proc, "cgroup")) })
            if (text is not null && DockerId().Match(text) is { Success: true } match)
                return match.Groups["id"].Value;

        return null;
    }

    [GeneratedRegex("(?:/containers/|/docker/|docker-)(?<id>[0-9a-f]{64})")]
    private static partial Regex DockerId();
}

/// <summary>
/// Reads a container's cgroup — v2 (<c>cpu.max</c>, <c>memory.current</c>) or, on an older host, v1
/// (<c>cpu.cfs_quota_us</c>, <c>memory.usage_in_bytes</c>). The roots are parameters for the tests.
/// </summary>
public sealed class ContainerMetrics(string runtime, string root = "/")
{
    private readonly string _cgroup = Path.Combine(root, "sys", "fs", "cgroup");
    private readonly string _proc = Path.Combine(root, "proc");
    private (long UsageMicroseconds, long AtTicks)? _previous;
    private double? _coresUsed;

    private bool IsVersion2 => File.Exists(Path.Combine(_cgroup, "cgroup.controllers"));

    /// <summary>
    /// The CPU the container used since the previous call, in cores (1.5 = one and a half cores
    /// busy); null on the first call. Remembered for <see cref="Read"/>.
    /// </summary>
    public double? SampleCpuCores()
    {
        if (ReadCpuUsageMicroseconds() is not { } usage)
            return _coresUsed = null;

        var now = Stopwatch.GetTimestamp();
        var previous = _previous;
        _previous = (usage, now);

        if (previous is not { } before || usage < before.UsageMicroseconds)
            return _coresUsed = null;

        var elapsed = Stopwatch.GetElapsedTime(before.AtTicks, now).TotalMicroseconds;
        return _coresUsed = elapsed <= 0 ? null : (usage - before.UsageMicroseconds) / elapsed;
    }

    /// <summary>The container's figures, with the CPU use the last <see cref="SampleCpuCores"/> measured.</summary>
    public ContainerStatus Read()
    {
        var limit = ReadCpuLimitCores();
        var (memoryUsed, memoryLimit) = ReadMemory();
        var (processes, processLimit) = ReadProcesses();
        var network = LinuxProc.ReadNetwork(_proc);
        var cores = _coresUsed;
        var available = limit ?? Environment.ProcessorCount;

        return new ContainerStatus(
            runtime,
            ContainerDetector.ContainerId(root),
            Environment.MachineName,
            LinuxProc.ReadOsRelease(Path.Combine(root, "etc")),
            ReadUptimeSeconds(),
            limit,
            cores,
            cores is { } used ? Math.Clamp(100 * used / available, 0, 100) : null,
            memoryUsed,
            memoryLimit,
            processes,
            processLimit,
            network?.Received,
            network?.Sent);
    }

    /// <summary>Total CPU time the container has used, in microseconds.</summary>
    internal long? ReadCpuUsageMicroseconds()
    {
        if (IsVersion2)
            return Field(LinuxProc.ReadLines(Path.Combine(_cgroup, "cpu.stat")), "usage_usec");

        // v1 counts nanoseconds.
        return Number(Read("cpuacct", "cpuacct.usage") ?? Read("cpu,cpuacct", "cpuacct.usage")) / 1000;
    }

    /// <summary>The CPU limit in cores, or null when the container may use every core.</summary>
    internal double? ReadCpuLimitCores()
    {
        if (IsVersion2)
        {
            // "max 100000" is no limit; "150000 100000" is one and a half cores.
            var fields = LinuxProc.ReadText(Path.Combine(_cgroup, "cpu.max"))?.Split(' ');
            return fields is [var quota, var period] && Number(quota) is { } q && Number(period) is { } p && p > 0
                ? (double)q / p
                : null;
        }

        var quotaV1 = Number(Read("cpu", "cpu.cfs_quota_us") ?? Read("cpu,cpuacct", "cpu.cfs_quota_us"));
        var periodV1 = Number(Read("cpu", "cpu.cfs_period_us") ?? Read("cpu,cpuacct", "cpu.cfs_period_us"));
        return quotaV1 is > 0 && periodV1 is > 0 ? (double)quotaV1 / periodV1 : null;
    }

    /// <summary>Memory in use and the limit, null for no limit.</summary>
    internal (long? Used, long? Limit) ReadMemory()
    {
        if (IsVersion2)
            return (Number(LinuxProc.ReadText(Path.Combine(_cgroup, "memory.current"))),
                Number(LinuxProc.ReadText(Path.Combine(_cgroup, "memory.max"))));

        // v1 writes "no limit" as a number near 2^63, rounded down to a page.
        var limit = Number(Read("memory", "memory.limit_in_bytes"));
        return (Number(Read("memory", "memory.usage_in_bytes")), limit is > 0 and < long.MaxValue / 2 ? limit : null);
    }

    internal (long? Current, long? Limit) ReadProcesses()
    {
        var directory = IsVersion2 ? _cgroup : Path.Combine(_cgroup, "pids");
        return (Number(LinuxProc.ReadText(Path.Combine(directory, "pids.current"))),
            Number(LinuxProc.ReadText(Path.Combine(directory, "pids.max"))));
    }

    /// <summary>
    /// How long the container has run: its first process's start, which in a Docker container started
    /// with an exec-form entrypoint is this server.
    /// </summary>
    private double? ReadUptimeSeconds()
    {
        try
        {
            using var first = Process.GetProcessById(1);
            return (DateTime.Now - first.StartTime).TotalSeconds;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException
                                       or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private string? Read(string controller, string file) => LinuxProc.ReadText(Path.Combine(_cgroup, controller, file));

    /// <summary>A whole number, or null for "max", an empty file or a missing one.</summary>
    private static long? Number(string? text) =>
        long.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static long? Field(string[]? lines, string name) =>
        lines?.Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(fields => fields.Length == 2 && fields[0] == name)
            .Select(fields => Number(fields[1]))
            .FirstOrDefault();
}
