using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using RaindropServer.Attachments;
using RaindropServer.Channels.Voice;
using RaindropServer.Dashboard.Metrics;
using RaindropServer.Data;
using RaindropServer.Events;
using RaindropServer.Security;
using RaindropServer.Users;

namespace RaindropServer.Dashboard;

/// <summary>Everything the dashboard shows, read at once.</summary>
public sealed record DashboardStatus(
    DateTime GeneratedAtUtc,
    ServerSection Server,
    ProcessStatus Process,
    ClientSection Clients,
    AccountSection Accounts,
    StorageSection Storage,
    EnvironmentSection Environment);

/// <summary>The server itself: which build, since when, and what it is set up to do.</summary>
public sealed record ServerSection(
    string Version,
    string ProtocolVersion,
    DateTime StartedAtUtc,
    double UptimeSeconds,
    string DotnetRuntime,
    string Database,
    string Voice,
    bool AttachmentsEnabled,
    string? TlsFingerprint,
    string? IdentityFingerprint,
    IReadOnlyList<string> Capabilities);

/// <summary>Who is connected right now.</summary>
/// <param name="EventStreams">Open event streams — one per running client.</param>
/// <param name="MembersOnline">Members with at least one stream open.</param>
/// <param name="Sessions">Signed-in sessions, connected or not.</param>
/// <param name="VoiceParticipants">Members in a voice call.</param>
/// <param name="VoiceCalls">Voice channels with somebody in them.</param>
public sealed record ClientSection(int EventStreams, int MembersOnline, int Sessions, int VoiceParticipants, int VoiceCalls);

/// <summary>Accounts on this server, by where they are in their lifecycle.</summary>
public sealed record AccountSection(long Active, long LeftDataPreserved, long Erased);

/// <summary>Encrypted files and the room left where the server keeps its data.</summary>
public sealed record StorageSection(
    bool AttachmentsEnabled,
    long AttachedFiles,
    long AttachedBytes,
    long PendingUploads,
    long PendingBytes,
    IReadOnlyList<VolumeStatus> Volumes);

/// <summary>One mounted volume the server writes to.</summary>
public sealed record VolumeStatus(string Purpose, string Path, string Mount, long TotalBytes, long FreeBytes);

/// <summary>
/// Where the server runs — exactly one of <see cref="Host"/> and <see cref="Container"/>. In a container
/// the machine underneath is left out: it is not the owner's to act on from here.
/// </summary>
/// <param name="Kind"><c>host</c> or <c>container</c>.</param>
/// <param name="Runtime"><c>docker</c>, <c>container</c> for another runtime, or null on a machine of its own.</param>
public sealed record EnvironmentSection(string Kind, string? Runtime, HostStatus? Host, ContainerStatus? Container);

/// <summary>Reads <see cref="DashboardStatus"/> from the running server's own services.</summary>
public sealed class DashboardStatusService
{
    private readonly IServiceProvider _services;
    private readonly ServerBuildInfo _build;
    private readonly ServerIdentity _identity;
    private readonly ProcessMetrics _process;
    private readonly DashboardEnvironment _environment;
    private readonly DashboardSampler _sampler;
    private readonly DashboardOptions _options;
    private readonly TimeProvider _time;

    public DashboardStatusService(
        IServiceProvider services,
        ServerBuildInfo build,
        ServerIdentity identity,
        ProcessMetrics process,
        DashboardEnvironment environment,
        DashboardSampler sampler,
        DashboardOptions options,
        TimeProvider? time = null)
    {
        _services = services;
        _build = build;
        _identity = identity;
        _process = process;
        _environment = environment;
        _sampler = sampler;
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    public async Task<DashboardStatus> ReadAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        return new DashboardStatus(
            now,
            ReadServer(now),
            _process.Read(),
            await ReadClientsAsync(cancellationToken),
            await ReadAccountsAsync(cancellationToken),
            await ReadStorageAsync(cancellationToken),
            ReadEnvironment());
    }

    private ServerSection ReadServer(DateTime now)
    {
        var attachments = _services.GetService<AttachmentOptions>();

        return new ServerSection(
            _build.Version,
            _identity.ProtocolVersion,
            _build.StartedAtUtc,
            Math.Max(0, (now - _build.StartedAtUtc).TotalSeconds),
            RuntimeInformation.FrameworkDescription,
            _services.GetService<DatabaseOptions>()?.Provider ?? "none",
            _services.GetService<IVoiceTransport>()?.Name ?? "none",
            attachments?.Enabled ?? false,
            _identity.TlsFingerprint,
            _identity.IdentityKey is { } key ? ServerCertificate.FormatFingerprint(SHA256.HashData(key)) : null,
            _identity.Capabilities);
    }

    private async Task<ClientSection> ReadClientsAsync(CancellationToken cancellationToken)
    {
        var connections = _services.GetRequiredService<IEventConnectionRegistry>();
        var sessions = _services.GetRequiredService<ISessionStore>();
        var voice = _services.GetService<IVoiceStateStore>();
        var participants = voice is null ? [] : await voice.ListAsync(null, cancellationToken);

        return new ClientSection(
            connections.Count,
            connections.ConnectedUserIds().Count,
            sessions.Count,
            participants.Count,
            participants.Select(participant => participant.ChannelId).Distinct().Count());
    }

    private async Task<AccountSection> ReadAccountsAsync(CancellationToken cancellationToken)
    {
        var users = _services.GetRequiredService<IUserStore>();

        return new AccountSection(
            await users.CountByStatusAsync(UserStatus.Active, cancellationToken),
            await users.CountByStatusAsync(UserStatus.LeftDataPreserved, cancellationToken),
            await users.CountByStatusAsync(UserStatus.Erased, cancellationToken));
    }

    private async Task<StorageSection> ReadStorageAsync(CancellationToken cancellationToken)
    {
        var options = _services.GetService<AttachmentOptions>();
        var statistics = _services.GetService<IAttachmentStore>() is { } store
            ? await store.GetStatisticsAsync(cancellationToken)
            : new AttachmentStatistics(0, 0, 0, 0, 0);

        var folders = new List<(string Purpose, string Path)>
        {
            ("server", AppContext.BaseDirectory),
            ("dashboard account", Path.GetDirectoryName(Path.GetFullPath(_options.CredentialsPath)) ?? AppContext.BaseDirectory)
        };

        if (options is { Enabled: true, StoragePath.Length: > 0 })
            folders.Add(("attachments", options.StoragePath));

        return new StorageSection(
            options?.Enabled ?? false,
            statistics.Attached,
            statistics.AttachedBytes,
            statistics.Pending,
            statistics.PendingBytes,
            Volumes(folders));
    }

    private EnvironmentSection ReadEnvironment()
    {
        if (_environment.Container is { } container)
            return new EnvironmentSection("container", _environment.Runtime, null, container.Read());

        return new EnvironmentSection("host", null, _environment.Host!.Read(_sampler.SystemCpuPercent), null);
    }

    /// <summary>
    /// The volume each folder lives on — the mount with the longest root that contains it — each
    /// volume once, named by the first folder found on it.
    /// </summary>
    private static List<VolumeStatus> Volumes(IEnumerable<(string Purpose, string Path)> folders)
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives().Where(drive => drive.IsReady).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var volumes = new List<VolumeStatus>();

        foreach (var (purpose, folder) in folders)
        {
            var path = Path.GetFullPath(folder);
            var drive = drives
                .Where(candidate => path.StartsWith(candidate.RootDirectory.FullName, StringComparison.Ordinal))
                .MaxBy(candidate => candidate.RootDirectory.FullName.Length);

            if (drive is null || volumes.Any(volume => volume.Mount == drive.RootDirectory.FullName))
                continue;

            try
            {
                volumes.Add(new VolumeStatus(purpose, path, drive.RootDirectory.FullName, drive.TotalSize, drive.AvailableFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return volumes;
    }
}
