using System.Data.Common;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using RaindropServer;
using RaindropServer.Channels;
using RaindropServer.Channels.Text;
using RaindropServer.Channels.Voice;
using RaindropServer.Data;
using RaindropServer.Events;
using RaindropServer.Roles;
using RaindropServer.Security;
using RaindropServer.Users;
using RaindropServer.Voice.LiveKit;
using ServerCli;

// ── 1. Read the server owner's configuration ─────────────────────────────────
// raindrop.json sits next to the executable; RAINDROP_* environment variables override it.
var config = ServerConfig.Load();

// ── 2. Configure services ────────────────────────────────────────────────────
// AddRaindropServer brings in the user subsystem and discovers every endpoint automatically —
// to add one, just implement an endpoint interface anywhere in the project. That's it.
// AddRaindropDatabase enables one of the installed database modules; nothing is stored until it is.
IServiceCollection services;
try
{
    services = new ServiceCollection();

    // The media server goes in first: the voice subsystem only fills in a transport when there is
    // none yet, and the one it would fill in refuses every join.
    if (config.VoiceProvider == "livekit")
        services.AddRaindropLiveKit(config.ApplyTo);

    services.AddRaindropServer(config.ApplyTo, config.ApplyTo, config.ApplyTo, configureVoice: config.ApplyTo);
}
catch (InvalidOperationException ex)
{
    // A permission name that does not exist, or a media server named but not described, is the server
    // owner's typo to fix, not a stack trace.
    Console.Error.WriteLine($"Cannot start Raindrop: {ex.Message}");
    return 1;
}

// ── 3. Enable the database and create any missing tables ─────────────────────
// Done before the server starts listening, so the first request cannot arrive without a schema.
try
{
    services.AddRaindropDatabase(config.ApplyTo);
    await services.InitializeRaindropDatabaseAsync();
}
catch (Exception ex) when (ex is InvalidOperationException or DbException)
{
    // A wrong driver name or an unreachable database is the server owner's typo to fix, not a crash
    // to hand them a stack trace for.
    Console.Error.WriteLine($"Cannot start Raindrop: {ex.Message}");
    Console.Error.WriteLine($"Check the 'database' section of raindrop.json. Installed drivers: {DatabaseDriverRegistry.DescribeAvailable()}.");
    return 1;
}

// ── 4. Load the TLS certificate, generating one on first start ───────────────
ServerHostOptions hostOptions;
try
{
    hostOptions = config.CreateHostOptions();

    // Checked here rather than left to ServerHost, so an https:// address with TLS switched off is
    // reported like the configuration mistake it is.
    hostOptions.Validate();
}
catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException
                               or InvalidOperationException)
{
    Console.Error.WriteLine($"Cannot start Raindrop: {ex.Message}");
    Console.Error.WriteLine("Check the 'urls' and 'tls' sections of raindrop.json.");
    return 1;
}

var fingerprint = hostOptions.Certificate is { } certificate ? ServerCertificate.Fingerprint(certificate) : null;

// The long-lived key that vouches for the certificate. Signing the fingerprint on every start is what
// lets the owner swap raindrop.pfx: clients that pinned this key follow the change instead of refusing.
ServerIdentityKey identityKey;
try
{
    identityKey = config.LoadIdentityKey();
}
catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException
                               or ArgumentException)
{
    Console.Error.WriteLine($"Cannot start Raindrop: the server identity key could not be loaded: {ex.Message}");
    Console.Error.WriteLine("Check 'tls.identityKeyPath' in raindrop.json.");
    return 1;
}

services.AddSingleton(identityKey);
services.AddSingleton(new ServerIdentity(
    ServerIdentity.CurrentProtocolVersion,
    fingerprint,
    identityKey.PublicKey,
    fingerprint is null ? null : identityKey.SignTlsBinding(fingerprint))
{
    Capabilities = fingerprint is null
        ? [ServerIdentity.E2eeDevicesCapability, ServerIdentity.E2eeDeliveryCapability, ServerIdentity.E2eeVoiceCapability]
        : [ServerIdentity.TlsIdentityBindingCapability, ServerIdentity.E2eeDevicesCapability,
            ServerIdentity.E2eeDeliveryCapability, ServerIdentity.E2eeVoiceCapability]
});
var identityFingerprint = ServerCertificate.FormatFingerprint(SHA256.HashData(identityKey.PublicKey));

// ── 5. Start the HTTP server on a background thread ──────────────────────────
// ServerHost.Start() launches ASP.NET Core in a Task.Run so this thread is free.
await using var server = ServerHost.Start(services, hostOptions);

try
{
    // Binding happens on the background thread; without this the console would cheerfully announce
    // a server that never got hold of its port.
    await server.WaitUntilStartedAsync();
}
catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException)
{
    Console.Error.WriteLine($"Cannot start Raindrop: {ex.Message}");
    return 1;
}

Console.WriteLine($"Raindrop server is running on {string.Join(", ", config.Urls)}");
Console.WriteLine($"Database: {config.Database.Provider}");

if (fingerprint is null)
    Console.WriteLine("TLS: off — only do this behind a proxy that terminates it for you.");
else
{
    Console.WriteLine($"TLS fingerprint (SHA-256): {fingerprint}");

    if (hostOptions.SslProtocols.HasFlag(SslProtocols.Tls12))
        Console.WriteLine(ServerHostOptions.PlatformServesTls13
            ? "TLS: 1.2 and 1.3 (tls.allowTls12 is on)."
            : "TLS: 1.2 and 1.3 — this platform cannot serve TLS 1.3 alone; run in Docker or on Linux for 1.3-only.");
}

Console.WriteLine($"Server identity (SHA-256): {identityFingerprint}");

Console.WriteLine(config.VoiceProvider is null
    ? "Voice: off — set voice.provider in raindrop.json to let members join calls."
    : $"Voice: {config.VoiceProvider} at {config.Voice.LiveKit?.ApiUrl}");

Console.WriteLine("Type 'help' for commands, 'quit' to stop.\n");

// Granting a role from the terminal has to go through the very service that is answering requests,
// or it would be writing behind the running server's back — and its permission cache would not know.
var roles = server.Services.GetRequiredService<RoleService>();
var events = server.Services.GetRequiredService<IEventConnectionRegistry>();
var voiceState = server.Services.GetRequiredService<IVoiceStateStore>();
var channelStore = server.Services.GetRequiredService<IChannelStore>();
var userStore = server.Services.GetRequiredService<IUserStore>();

// ── 6. Main thread: the server owner's console ───────────────────────────────
// The server runs independently; use this loop for your own CLI commands.
while (true)
{
    var input = Console.ReadLine()?.Trim();

    if (input is null)
    {
        // No console to read from — a container started without a TTY, say. There is nobody to take
        // commands from, so keep serving until the process is asked to stop.
        await WaitForStopSignalAsync();
        break;
    }

    if (input.Equals("quit", StringComparison.OrdinalIgnoreCase))
        break;

    // Split before the switch: grant and revoke take arguments, and a role name may contain spaces
    // where a username may not, so the username is one word and the role is everything after it.
    var words = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    switch (words.Length == 0 ? string.Empty : words[0].ToLowerInvariant())
    {
        case "":
            break;

        case "help":
            Console.WriteLine("  drivers                  list the installed database modules");
            Console.WriteLine("  db                       show which database this server is using");
            Console.WriteLine("  fingerprint              show the TLS and identity fingerprints clients pin");
            Console.WriteLine("  roles                    list this server's roles and what they grant");
            Console.WriteLine("  grant <user> <role>      give a member a role");
            Console.WriteLine("  revoke <user> <role>     take a role away from a member");
            Console.WriteLine("  events                   show how many clients are listening for events");
            Console.WriteLine("  voice                    list who is in which voice call");
            Console.WriteLine("  purge-plaintext          delete every message stored before end-to-end encryption");
            Console.WriteLine("  reports                  show the most recently reported messages");
            Console.WriteLine("  quit                     stop the server");
            break;

        case "roles":
            await PrintRolesAsync();
            break;

        case "grant":
        case "revoke":
            await ChangeRoleAsync(words);
            break;

        case "events":
            Console.WriteLine($"  {events.Count} event stream(s) open");
            break;

        case "voice":
            await PrintVoiceCallsAsync();
            break;

        case "reports":
            await PrintReportsAsync();
            break;

        case "purge-plaintext":
            await PurgePlaintextAsync();
            break;

        case "fingerprint":
            Console.WriteLine(fingerprint is null
                ? "  TLS is off, so this server has no fingerprint of its own."
                : $"  TLS:      {fingerprint}");
            Console.WriteLine($"  identity: {identityFingerprint}");
            break;

        case "drivers":
            Console.WriteLine($"  installed: {DatabaseDriverRegistry.DescribeAvailable()}");
            Console.WriteLine($"  enabled:   {config.Database.Provider}");
            break;

        case "db":
            Console.WriteLine($"  provider:  {config.Database.Provider}");
            Console.WriteLine($"  retention: {config.Users.DataRetentionDays} days after a user leaves");
            break;

        default:
            Console.WriteLine($"Unknown command '{input}'. Type 'help'.");
            break;
    }
}

Console.WriteLine("Shutting down...");
return 0;

// Prints every role and what it grants — the console's answer to "who can do what on this server".
async Task PrintRolesAsync()
{
    foreach (var role in await roles.ListForConsoleAsync(CancellationToken.None))
    {
        var granted = PermissionNames.Of(role.Permissions);

        Console.WriteLine($"  {role.Name}  [{RoleKindNames.Of(role.Kind)}]");
        Console.WriteLine($"      {(granted.Count == 0 ? "nothing" : string.Join(", ", granted))}");
    }
}

// Lists every voice call and who is in it — read from the running server's own record of them.
async Task PrintVoiceCallsAsync()
{
    var participants = await voiceState.ListAsync(null, CancellationToken.None);

    if (participants.Count == 0)
    {
        Console.WriteLine("  nobody is in a voice call");
        return;
    }

    foreach (var call in participants.GroupBy(participant => participant.ChannelId))
    {
        var channel = await channelStore.FindByIdAsync(call.Key, CancellationToken.None);
        Console.WriteLine($"  {channel?.Name ?? call.Key.ToString()}");

        foreach (var participant in call)
        {
            var user = await userStore.FindByIdAsync(participant.UserId, CancellationToken.None);
            var flags = new List<string>();
            if (!participant.Connected) flags.Add("connecting");
            if (participant.SelfDeaf) flags.Add("deafened");
            else if (participant.SelfMute) flags.Add("muted");
            if (participant.ServerMute) flags.Add("muted for everyone");
            if (participant.Suppressed) flags.Add("cannot speak");

            Console.WriteLine($"      {user?.Username ?? participant.UserId.ToString()}" +
                              (flags.Count == 0 ? "" : $"  ({string.Join(", ", flags)})"));
        }
    }
}

// The latest reports: encrypted messages members revealed to moderators, each proven by its author's
// device signature.
async Task PrintReportsAsync()
{
    var reports = await server.Services.GetRequiredService<IMessageReportStore>().ListRecentAsync(20, CancellationToken.None);
    if (reports.Count == 0)
    {
        Console.WriteLine("  no messages have been reported");
        return;
    }

    foreach (var report in reports)
    {
        var channel = await channelStore.FindByIdAsync(report.ChannelId, CancellationToken.None);
        var author = await userStore.FindByIdAsync(report.AuthorId, CancellationToken.None);
        var reporter = await userStore.FindByIdAsync(report.ReporterId, CancellationToken.None);
        Console.WriteLine($"  {report.ReportedAtUtc:u}  #{channel?.Name ?? report.ChannelId.ToString()}  " +
                          $"{author?.Username ?? report.AuthorId.ToString()} (reported by {reporter?.Username ?? report.ReporterId.ToString()})");
        Console.WriteLine($"      \"{report.Body}\"" + (report.Reason is null ? "" : $"  — {report.Reason}"));
    }
}

// Deletes every plaintext message and the archive of deleted messages' text: everything the server
// could read. Encrypted messages stay. Asks first, since it cannot be undone.
async Task PurgePlaintextAsync()
{
    Console.Write("  Delete every plaintext message and the deleted-message archive? This cannot be undone. Type 'yes': ");
    if (!string.Equals(Console.ReadLine()?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("  Nothing was deleted.");
        return;
    }

    try
    {
        var messages = await server.Services.GetRequiredService<ITextMessageStore>().PurgePlaintextAsync(CancellationToken.None);
        var archived = await server.Services.GetRequiredService<IDeletedMessageStore>().PurgeAsync(CancellationToken.None);
        Console.WriteLine($"  Deleted {messages} plaintext message(s) and {archived} archived message(s).");
    }
    catch (Exception ex) when (ex is DbException or NotSupportedException)
    {
        Console.WriteLine($"  {ex.Message}");
    }
}

// Grants or revokes a role from the terminal. These bypass every permission check by design: whoever
// is at the console owns the machine, the database and the process, so there is nothing left for a
// check to protect. It is the recovery path for a server whose owner left, and the only way to grant
// the first role on a server with firstMemberBecomesOwner turned off.
async Task ChangeRoleAsync(string[] words)
{
    if (words.Length < 3)
    {
        Console.WriteLine($"  usage: {words[0]} <username> <role name>");
        return;
    }

    var username = words[1];
    var roleName = string.Join(' ', words[2..]);

    try
    {
        Console.WriteLine("  " + (words[0].Equals("grant", StringComparison.OrdinalIgnoreCase)
            ? await roles.GrantFromConsoleAsync(username, roleName, CancellationToken.None)
            : await roles.RevokeFromConsoleAsync(username, roleName, CancellationToken.None)));
    }
    catch (Exception ex) when (ex is InvalidOperationException or DbException)
    {
        Console.WriteLine($"  {ex.Message}");
    }
}

// Blocks until the process is asked to shut down, so the server outlives a closed stdin.
async Task WaitForStopSignalAsync()
{
    // RunContinuationsAsynchronously matters here: without it, everything that follows the await
    // below — the whole shutdown — would run inline on the runtime's signal-handling thread, the one
    // thread every PosixSignalRegistration in the process shares. Occupying it means no further
    // signal is processed while we shut down, so an impatient second SIGTERM or Ctrl-C is ignored
    // and the host's own handlers are held up behind ours.
    var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
    using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);

    await stopping.Task;

    void Stop(PosixSignalContext context)
    {
        context.Cancel = true;
        stopping.TrySetResult();
    }
}
