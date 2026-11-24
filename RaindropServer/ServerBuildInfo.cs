using System.Diagnostics;
using System.Reflection;

namespace RaindropServer;

/// <summary>
/// Which build of the server this is and when the process started — what the owner's dashboard shows
/// as the version and uptime. Not part of the client protocol: clients are told
/// <see cref="ServerIdentity.ProtocolVersion"/>, never the build.
/// </summary>
/// <param name="Version">The release version (stamped by the release workflow), or the build's own.</param>
/// <param name="StartedAtUtc">When this process started.</param>
public sealed record ServerBuildInfo(string Version, DateTime StartedAtUtc)
{
    /// <summary>The version <paramref name="assembly"/> was built as, and this process's start time.</summary>
    public static ServerBuildInfo For(Assembly assembly)
    {
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString()
                      ?? "unknown";

        DateTime started;
        try
        {
            using var process = Process.GetCurrentProcess();
            started = process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            started = DateTime.UtcNow;
        }

        return new ServerBuildInfo(version, started);
    }
}
