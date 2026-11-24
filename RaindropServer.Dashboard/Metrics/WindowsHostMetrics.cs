using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RaindropServer.Dashboard.Metrics;

/// <summary>Windows, through <c>GetSystemTimes</c> and <c>GlobalMemoryStatusEx</c>.</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsHostMetrics : HostMetrics
{
    protected override (ulong Busy, ulong Total)? ReadCpuTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return null;

        // Kernel time includes the idle time.
        var total = kernel + user;
        return (total - idle, total);
    }

    protected override (long Total, long Available)? ReadMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? ((long)status.TotalPhysical, (long)status.AvailablePhysical) : null;
    }

    protected override string? CpuModel() => Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
