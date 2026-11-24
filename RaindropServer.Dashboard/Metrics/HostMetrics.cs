using System.Globalization;
using System.Runtime.InteropServices;

namespace RaindropServer.Dashboard.Metrics;

/// <summary>The machine the server runs on, as the dashboard shows it.</summary>
public sealed record HostStatus(
    string OperatingSystem,
    string Kernel,
    string Architecture,
    string MachineName,
    string? CpuModel,
    int CpuCount,
    double? CpuPercent,
    long? MemoryTotalBytes,
    long? MemoryAvailableBytes,
    double[]? LoadAverage,
    double? UptimeSeconds);

/// <summary>
/// Reads the machine's own figures. One implementation per operating system, because none of them is
/// in .NET: Linux has <c>/proc</c>, macOS and Windows have system calls. What a platform cannot answer
/// is null, and the page leaves it out.
/// </summary>
public abstract class HostMetrics
{
    private (ulong Busy, ulong Total)? _previous;

    /// <summary>The implementation for the operating system this is running on.</summary>
    public static HostMetrics ForThisPlatform() =>
        OperatingSystem.IsLinux() ? new LinuxHostMetrics() :
        OperatingSystem.IsMacOS() ? new MacHostMetrics() :
        OperatingSystem.IsWindows() ? new WindowsHostMetrics() :
        new UnknownHostMetrics();

    /// <summary>The CPU in use across every core since the previous call, as a percentage; null on the first.</summary>
    public double? SampleCpuPercent()
    {
        var times = ReadCpuTimes();
        if (times is not { } now)
            return null;

        var previous = _previous;
        _previous = now;

        if (previous is not { } before || now.Total <= before.Total)
            return null;

        return Math.Clamp(100.0 * (now.Busy - before.Busy) / (now.Total - before.Total), 0, 100);
    }

    /// <summary>Everything but the CPU percentage, which the sampler measures over time.</summary>
    public HostStatus Read(double? cpuPercent)
    {
        var memory = ReadMemory();

        return new HostStatus(
            OperatingSystemName() ?? RuntimeInformation.OSDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            Environment.MachineName,
            CpuModel(),
            Environment.ProcessorCount,
            cpuPercent,
            memory?.Total,
            memory?.Available,
            ReadLoadAverage(),
            ReadUptimeSeconds());
    }

    /// <summary>Busy and total CPU time across every core, in whatever unit the platform counts.</summary>
    protected abstract (ulong Busy, ulong Total)? ReadCpuTimes();

    /// <summary>Physical memory, and how much of it could be given to programs now.</summary>
    protected abstract (long Total, long Available)? ReadMemory();

    protected virtual double[]? ReadLoadAverage() => null;

    protected virtual double? ReadUptimeSeconds() => Environment.TickCount64 / 1000.0;

    protected virtual string? CpuModel() => null;

    protected virtual string? OperatingSystemName() => null;
}

/// <summary>A platform this build has no readers for: what .NET itself knows, and nothing more.</summary>
internal sealed class UnknownHostMetrics : HostMetrics
{
    protected override (ulong Busy, ulong Total)? ReadCpuTimes() => null;

    protected override (long Total, long Available)? ReadMemory() => null;
}

/// <summary>Linux, from <c>/proc</c> and <c>/etc/os-release</c>. The roots are parameters for the tests.</summary>
internal sealed class LinuxHostMetrics(string proc = "/proc", string etc = "/etc") : HostMetrics
{
    protected override (ulong Busy, ulong Total)? ReadCpuTimes() => LinuxProc.ReadCpuTimes(proc);

    protected override (long Total, long Available)? ReadMemory() => LinuxProc.ReadMemory(proc);

    protected override double[]? ReadLoadAverage() => LinuxProc.ReadLoadAverage(proc);

    protected override double? ReadUptimeSeconds() => LinuxProc.ReadUptimeSeconds(proc);

    protected override string? CpuModel() => LinuxProc.ReadCpuModel(proc);

    protected override string? OperatingSystemName() => LinuxProc.ReadOsRelease(etc);
}

/// <summary>Parsers for the Linux files both the host and the container readers use.</summary>
internal static class LinuxProc
{
    /// <summary>The <c>cpu</c> line of <c>/proc/stat</c>: busy is everything but idle and iowait.</summary>
    public static (ulong Busy, ulong Total)? ReadCpuTimes(string proc)
    {
        var line = ReadLines(Path.Combine(proc, "stat"))?.FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
        if (line is null)
            return null;

        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(field => ulong.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0)
            .ToArray();

        if (fields.Length < 4)
            return null;

        // user nice system idle iowait irq softirq steal [guest guest_nice — already counted in user]
        var total = fields.Take(8).Aggregate(0UL, (sum, value) => sum + value);
        var idle = fields[3] + (fields.Length > 4 ? fields[4] : 0);
        return (total - idle, total);
    }

    /// <summary><c>MemTotal</c> and <c>MemAvailable</c> from <c>/proc/meminfo</c>, in bytes.</summary>
    public static (long Total, long Available)? ReadMemory(string proc)
    {
        long? total = null, available = null;

        foreach (var line in ReadLines(Path.Combine(proc, "meminfo")) ?? [])
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                total = Kilobytes(line);
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                available = Kilobytes(line);
        }

        return total is { } t && available is { } a ? (t, a) : null;

        static long? Kilobytes(string line) =>
            long.TryParse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1),
                NumberStyles.None, CultureInfo.InvariantCulture, out var kb)
                ? kb * 1024
                : null;
    }

    public static double[]? ReadLoadAverage(string proc)
    {
        var fields = ReadText(Path.Combine(proc, "loadavg"))?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields is not { Length: >= 3 })
            return null;

        var values = fields.Take(3)
            .Select(field => double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN)
            .ToArray();

        return values.Any(double.IsNaN) ? null : values;
    }

    public static double? ReadUptimeSeconds(string proc) =>
        double.TryParse(ReadText(Path.Combine(proc, "uptime"))?.Split(' ').FirstOrDefault(),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : null;

    /// <summary>The <c>model name</c> of the first core; ARM boards often have none.</summary>
    public static string? ReadCpuModel(string proc) =>
        ReadLines(Path.Combine(proc, "cpuinfo"))?
            .Where(line => line.StartsWith("model name", StringComparison.Ordinal))
            .Select(line => line[(line.IndexOf(':') + 1)..].Trim())
            .FirstOrDefault(model => model.Length > 0);

    /// <summary><c>PRETTY_NAME</c> from <c>os-release</c>, e.g. "Debian GNU/Linux 12 (bookworm)".</summary>
    public static string? ReadOsRelease(string etc) =>
        ReadLines(Path.Combine(etc, "os-release"))?
            .Where(line => line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
            .Select(line => line["PRETTY_NAME=".Length..].Trim().Trim('"'))
            .FirstOrDefault(name => name.Length > 0);

    /// <summary>Received and sent bytes across every interface but loopback, from <c>/proc/net/dev</c>.</summary>
    public static (long Received, long Sent)? ReadNetwork(string proc)
    {
        var lines = ReadLines(Path.Combine(proc, "net", "dev"));
        if (lines is null)
            return null;

        long received = 0, sent = 0;
        foreach (var line in lines.Skip(2))
        {
            var colon = line.IndexOf(':');
            if (colon < 0 || line[..colon].Trim() == "lo")
                continue;

            var fields = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 9)
                continue;

            received += long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var rx) ? rx : 0;
            sent += long.TryParse(fields[8], NumberStyles.None, CultureInfo.InvariantCulture, out var tx) ? tx : 0;
        }

        return (received, sent);
    }

    public static string[]? ReadLines(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllLines(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
