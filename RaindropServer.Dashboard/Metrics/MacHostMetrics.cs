using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace RaindropServer.Dashboard.Metrics;

/// <summary>
/// macOS, through the Mach host calls and <c>sysctl</c> — the same figures Activity Monitor reads.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed partial class MacHostMetrics : HostMetrics
{
    private const string LibSystem = "/usr/lib/libSystem.dylib";
    private const int HostCpuLoadInfo = 3;
    private const int HostVmInfo64 = 4;

    // Mach hands out one send right per call; asking once keeps the count from growing every sample.
    private readonly uint _host = mach_host_self();

    protected override (ulong Busy, ulong Total)? ReadCpuTimes()
    {
        // host_cpu_load_info: ticks spent in user, system, idle and nice, summed over every core.
        var ticks = new uint[4];
        var count = ticks.Length;
        if (host_statistics(_host, HostCpuLoadInfo, ticks, ref count) != 0)
            return null;

        ulong user = ticks[0], system = ticks[1], idle = ticks[2], nice = ticks[3];
        var total = user + system + idle + nice;
        return (total - idle, total);
    }

    protected override (long Total, long Available)? ReadMemory()
    {
        var total = SysctlInt64("hw.memsize");
        var pageSize = SysctlInt64("hw.pagesize");
        if (total is null || pageSize is null)
            return null;

        // vm_statistics64: free_count, active_count, inactive_count, wire_count (32-bit each), then
        // eight 64-bit counters, then purgeable_count and speculative_count. A buffer larger than the
        // structure is fine; the kernel says how much it filled.
        var statistics = new int[64];
        var count = statistics.Length;
        if (host_statistics64(_host, HostVmInfo64, statistics, ref count) != 0 || count < 22)
            return (total.Value, 0);

        long free = (uint)statistics[0], inactive = (uint)statistics[2], speculative = (uint)statistics[21];
        return (total.Value, Math.Min(total.Value, (free + inactive + speculative) * pageSize.Value));
    }

    protected override double[]? ReadLoadAverage()
    {
        var load = new double[3];
        return getloadavg(load, load.Length) == 3 ? load : null;
    }

    protected override double? ReadUptimeSeconds()
    {
        // kern.boottime is a struct timeval; its first field is the seconds since the epoch.
        var buffer = new byte[16];
        var size = (nuint)buffer.Length;
        if (sysctlbyname("kern.boottime", buffer, ref size, IntPtr.Zero, 0) != 0 || size < 8)
            return base.ReadUptimeSeconds();

        var booted = BitConverter.ToInt64(buffer, 0);
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds() - booted;
    }

    protected override string? CpuModel() => SysctlString("machdep.cpu.brand_string");

    protected override string? OperatingSystemName() =>
        SysctlString("kern.osproductversion") is { } version ? $"macOS {version}" : null;

    private static long? SysctlInt64(string name)
    {
        var buffer = new byte[8];
        var size = (nuint)buffer.Length;
        if (sysctlbyname(name, buffer, ref size, IntPtr.Zero, 0) != 0)
            return null;

        return size switch
        {
            8 => BitConverter.ToInt64(buffer, 0),
            4 => BitConverter.ToInt32(buffer, 0),
            _ => null
        };
    }

    private static string? SysctlString(string name)
    {
        nuint size = 0;
        if (sysctlbyname(name, null, ref size, IntPtr.Zero, 0) != 0 || size == 0 || size > 1024)
            return null;

        var buffer = new byte[size];
        if (sysctlbyname(name, buffer, ref size, IntPtr.Zero, 0) != 0)
            return null;

        return Encoding.UTF8.GetString(buffer, 0, (int)size).TrimEnd('\0').Trim() is { Length: > 0 } text ? text : null;
    }

    [LibraryImport(LibSystem)]
    private static partial uint mach_host_self();

    [LibraryImport(LibSystem)]
    private static partial int host_statistics(uint host, int flavor, [Out] uint[] info, ref int count);

    [LibraryImport(LibSystem)]
    private static partial int host_statistics64(uint host, int flavor, [Out] int[] info, ref int count);

    [LibraryImport(LibSystem)]
    private static partial int getloadavg([Out] double[] load, int count);

    [LibraryImport(LibSystem, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int sysctlbyname(string name, [Out] byte[]? value, ref nuint size, IntPtr newValue, nuint newSize);
}
