using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;
using RaindropServer;
using RaindropServer.Channels;
using RaindropServer.Channels.Voice;
using RaindropServer.Data;
using RaindropServer.Roles;
using RaindropServer.Security;
using RaindropServer.Users;
using RaindropServer.Voice.LiveKit;

namespace ServerCli;

/// <summary>
/// The settings a server owner controls, read from <c>raindrop.json</c> next to the executable and
/// overridable through environment variables (which is how you configure the container).
/// </summary>
public sealed record ServerConfig
{
    private const string FileName = "raindrop.json";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>The URLs the server listens on. Use https:// unless something else terminates TLS.</summary>
    public string[] Urls { get; init; } = ["https://localhost:5001"];

    /// <summary>The certificate this server presents to clients.</summary>
    public TlsConfig Tls { get; init; } = new();

    /// <summary>Which database module to enable, and how to reach the database.</summary>
    public DatabaseConfig Database { get; init; } = new();

    /// <summary>Account, session and retention rules.</summary>
    public UserConfig Users { get; init; } = new();

    /// <summary>Channel and message rules.</summary>
    public ChannelConfig Channels { get; init; } = new();

    /// <summary>What roles a fresh server starts with, and how it decides who administers it.</summary>
    public RoleConfig Roles { get; init; } = new();

    /// <summary>Voice calls, and the media server that carries them.</summary>
    public VoiceConfig Voice { get; init; } = new();

    /// <summary>
    /// Loads the configuration file if there is one, then lets environment variables override it.
    /// A missing file is fine — the defaults run a SQLite server on localhost.
    /// </summary>
    public static ServerConfig Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, FileName);
        var config = new ServerConfig();

        if (File.Exists(path))
        {
            config = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), ReadOptions)
                     ?? throw new InvalidOperationException($"'{path}' is empty or not valid JSON.");
        }

        return config.WithEnvironmentOverrides();
    }

    private ServerConfig WithEnvironmentOverrides()
    {
        var urls = Environment.GetEnvironmentVariable("RAINDROP_URLS");

        return this with
        {
            Urls = string.IsNullOrWhiteSpace(urls)
                ? Urls
                : urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Database = Database with
            {
                Provider = Environment.GetEnvironmentVariable("RAINDROP_DB_PROVIDER") ?? Database.Provider,
                ConnectionString = Environment.GetEnvironmentVariable("RAINDROP_DB_CONNECTION_STRING")
                                   ?? Database.ConnectionString
            },
            Tls = Tls with
            {
                Enabled = Environment.GetEnvironmentVariable("RAINDROP_TLS_ENABLED") is { } enabled
                    ? enabled.Equals("true", StringComparison.OrdinalIgnoreCase)
                    : Tls.Enabled,
                CertificatePath = Environment.GetEnvironmentVariable("RAINDROP_TLS_CERTIFICATE_PATH") ?? Tls.CertificatePath,
                CertificatePassword = Environment.GetEnvironmentVariable("RAINDROP_TLS_CERTIFICATE_PASSWORD")
                                      ?? Tls.CertificatePassword
            },
            Voice = Voice with
            {
                Provider = Environment.GetEnvironmentVariable("RAINDROP_VOICE_PROVIDER") ?? Voice.Provider,
                LiveKit = (Voice.LiveKit ?? new LiveKitConfig()) with
                {
                    ApiUrl = Environment.GetEnvironmentVariable("RAINDROP_LIVEKIT_API_URL")
                             ?? Voice.LiveKit?.ApiUrl ?? new LiveKitConfig().ApiUrl,
                    ApiKey = Environment.GetEnvironmentVariable("RAINDROP_LIVEKIT_API_KEY") ?? Voice.LiveKit?.ApiKey,
                    ApiSecret = Environment.GetEnvironmentVariable("RAINDROP_LIVEKIT_API_SECRET") ?? Voice.LiveKit?.ApiSecret,
                    AllowWeakSecret = Environment.GetEnvironmentVariable("RAINDROP_LIVEKIT_ALLOW_WEAK_SECRET") is { } weak
                        ? weak.Equals("true", StringComparison.OrdinalIgnoreCase)
                        : Voice.LiveKit?.AllowWeakSecret ?? false
                }
            }
        };
    }

    /// <summary>
    /// The media server named by <c>voice.provider</c>, or none. Only <c>livekit</c> is built in.
    /// </summary>
    /// <exception cref="InvalidOperationException">The provider is unknown, or its settings are incomplete.</exception>
    public string? VoiceProvider =>
        string.IsNullOrWhiteSpace(Voice.Provider) || Voice.Provider.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? null
            : Voice.Provider.Trim().ToLowerInvariant() switch
            {
                "livekit" => "livekit",
                var other => throw new InvalidOperationException(
                    $"The 'voice.provider' setting in raindrop.json is '{other}'; the only media server this build knows is 'livekit' (or null for none).")
            };

    /// <summary>Applies the configured voice rules to the options the server reads.</summary>
    public void ApplyTo(VoiceOptions options)
    {
        options.MaximumParticipantsPerChannel = Voice.MaximumParticipants;
    }

    /// <summary>Applies the configured LiveKit connection to the options its module reads.</summary>
    public void ApplyTo(LiveKitOptions options)
    {
        var livekit = Voice.LiveKit ?? new LiveKitConfig();
        options.ApiUrl = livekit.ApiUrl;
        options.ApiKey = livekit.ApiKey ?? "";
        options.ApiSecret = livekit.ApiSecret ?? "";
        options.AllowWeakSecret = livekit.AllowWeakSecret;
    }

    /// <summary>Applies the configured database settings to the options the server reads.</summary>
    public void ApplyTo(DatabaseOptions options)
    {
        options.Provider = Database.Provider;
        options.ConnectionString = Database.ConnectionString;
        options.AutoCreateSchema = Database.AutoCreateSchema;
    }

    /// <summary>Applies the configured account rules to the options the server reads.</summary>
    public void ApplyTo(UserOptions options)
    {
        options.DataRetentionAfterLeaving = TimeSpan.FromDays(Users.DataRetentionDays);
        options.SessionIdleTimeout = TimeSpan.FromHours(Users.SessionIdleTimeoutHours);
        options.MinimumPasswordLength = Users.MinimumPasswordLength;
    }

    /// <summary>Applies the configured channel rules to the options the server reads.</summary>
    public void ApplyTo(ChannelOptions options)
    {
        options.MaximumMessageLength = Channels.MaximumMessageLength;
        options.DefaultHistoryPageSize = Channels.DefaultHistoryPageSize;
        options.MaximumHistoryPageSize = Channels.MaximumHistoryPageSize;
        options.AllowPlaintextMessages = Channels.AllowPlaintextMessages;
    }

    /// <summary>
    /// Applies the configured role rules to the options the server reads.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The configured default permissions name something this build does not have.
    /// </exception>
    public void ApplyTo(RoleOptions options)
    {
        // Only the seed for a server that has never had an everyone role. Once it exists, its
        // permissions are whatever this server's administrators have since made them, and editing
        // this setting does not reach back and overwrite their decision.
        if (Roles.DefaultMemberPermissions is { } names)
        {
            try
            {
                options.DefaultMemberPermissions = PermissionNames.Parse(names);
            }
            catch (ApiException ex)
            {
                throw new InvalidOperationException(
                    $"The 'roles.defaultMemberPermissions' setting in raindrop.json is not valid: {ex.Message}");
            }
        }

        options.ChannelCreatorModeratesOwnChannels = Roles.ChannelCreatorModeratesOwnChannels;
        options.FirstMemberBecomesOwner = Roles.FirstMemberBecomesOwner;
    }

    /// <summary>
    /// Builds the listening options, loading — or, on first start, generating and saving — the
    /// certificate. Returns the certificate too, so the caller can show its fingerprint.
    /// </summary>
    public ServerHostOptions CreateHostOptions()
    {
        var options = new ServerHostOptions
        {
            Urls = Urls,
            SslProtocols = Tls.AllowTls12 || !ServerHostOptions.PlatformServesTls13
                ? SslProtocols.Tls12 | SslProtocols.Tls13
                : SslProtocols.Tls13
        };

        if (Tls.Enabled)
            options.Certificate = ServerCertificate.LoadOrCreate(
                ResolvePath(Tls.CertificatePath),
                string.IsNullOrEmpty(Tls.CertificatePassword) ? null : Tls.CertificatePassword,
                CertificateHostNames());

        return options;
    }

    /// <summary>
    /// The names to put in the certificate: whatever the owner listed, or else the hosts derived from
    /// the listening URLs. A wildcard address names no host in particular, so this machine's own
    /// names stand in for it.
    /// </summary>
    private IReadOnlyCollection<string> CertificateHostNames()
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost" };

        if (Tls.HostNames is { Length: > 0 })
        {
            foreach (var host in Tls.HostNames)
                hosts.Add(host);

            return hosts;
        }

        foreach (var url in Urls)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                continue;

            if (uri.Host is "0.0.0.0" or "[::]" or "*" or "+")
                hosts.Add(Dns.GetHostName());
            else
                hosts.Add(uri.Host);
        }

        return hosts;
    }

    /// <summary>
    /// Loads — or, on first start, generates and saves — the key this server signs its certificate
    /// with, so clients can follow a certificate change without losing their pin.
    /// </summary>
    public ServerIdentityKey LoadIdentityKey() => ServerIdentityKey.LoadOrCreate(ResolvePath(Tls.IdentityKeyPath));

    /// <summary>Resolves a relative path against the executable, so it sits next to raindrop.json.</summary>
    private static string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
}

/// <param name="Enabled">
/// Whether the server terminates TLS itself. Turn it off only when a reverse proxy does it instead.
/// </param>
/// <param name="CertificatePath">
/// The PKCS#12 (.pfx) file to present, relative to the executable. Generated as a self-signed
/// certificate on first start if it is not there; point it at a CA-issued file to use a real one.
/// </param>
/// <param name="CertificatePassword">The certificate file's password, if it has one.</param>
/// <param name="HostNames">
/// The names and IP addresses clients reach this server by, for a generated certificate. Derived
/// from the listening URLs when left out.
/// </param>
/// <param name="IdentityKeyPath">
/// The server's long-lived signing key, relative to the executable, generated on first start. It
/// vouches for whichever certificate the server presents, which is what lets you replace
/// <paramref name="CertificatePath"/> without every client refusing to connect. Back it up with the
/// certificate; losing it makes the server a stranger to every client.
/// </param>
/// <param name="AllowTls12">
/// Whether clients may still negotiate TLS 1.2. Off by default: TLS 1.3 alone.
/// </param>
public sealed record TlsConfig(
    bool Enabled = true,
    string CertificatePath = "raindrop.pfx",
    string? CertificatePassword = null,
    string[]? HostNames = null,
    string IdentityKeyPath = "server-identity.key",
    bool AllowTls12 = false);

/// <param name="Provider">The database module to enable: sqlite, postgres, sqlserver, or a driver you added.</param>
/// <param name="ConnectionString">The ADO.NET connection string for that database.</param>
/// <param name="AutoCreateSchema">Whether the server may create its own tables at startup.</param>
public sealed record DatabaseConfig(
    string Provider = "sqlite",
    string ConnectionString = "Data Source=raindrop.db",
    bool AutoCreateSchema = true);

/// <param name="DataRetentionDays">
/// How long a departed user's preserved data is kept. Capped at 730 days — two years — by the server.
/// </param>
/// <param name="SessionIdleTimeoutHours">How long a session survives without a request.</param>
/// <param name="MinimumPasswordLength">The shortest password this server accepts.</param>
public sealed record UserConfig(
    int DataRetentionDays = 730,
    double SessionIdleTimeoutHours = 12,
    int MinimumPasswordLength = 8);

/// <param name="MaximumMessageLength">The longest message this server accepts, in characters.</param>
/// <param name="DefaultHistoryPageSize">How many messages a history request returns by default.</param>
/// <param name="MaximumHistoryPageSize">The most messages one history request may return.</param>
/// <param name="AllowPlaintextMessages">
/// Whether members may still post plaintext messages the server can read. Off by default: every
/// message must be end-to-end encrypted.
/// </param>
public sealed record ChannelConfig(
    int MaximumMessageLength = 4000,
    int DefaultHistoryPageSize = 50,
    int MaximumHistoryPageSize = 200,
    bool AllowPlaintextMessages = false);

/// <param name="DefaultMemberPermissions">
/// What the built-in <c>everyone</c> role is created with on a server that has never had one, as
/// permission names — ask <c>GET /api/roles/permissions</c> for the list this build understands.
/// Left out, a new member may see channels, post in them, and create channels. Only a seed: once the
/// role exists, changing this does not reach back and overwrite what administrators have made it.
/// </param>
/// <param name="ChannelCreatorModeratesOwnChannels">
/// Whether creating a channel makes you its moderator, whatever your roles say. On by default, so a
/// member who can create a channel can also tidy it up afterwards. Turn it off for a server where
/// roles are meant to be the only answer to who may do what.
/// </param>
/// <param name="FirstMemberBecomesOwner">
/// Whether the first UUID to register is granted the owner role. On by default: a self-hosted server
/// has no account handed down to administer it otherwise. Turn it off on a server whose owner is
/// granted the role from the console with <c>grant</c> instead.
/// </param>
public sealed record RoleConfig(
    string[]? DefaultMemberPermissions = null,
    bool ChannelCreatorModeratesOwnChannels = true,
    bool FirstMemberBecomesOwner = true);

/// <param name="Provider">
/// The media server that carries voice calls: <c>livekit</c>, or null for none — voice channels can
/// then still be created, but nobody can join one.
/// </param>
/// <param name="MaximumParticipants">The most members one voice call holds.</param>
/// <param name="LiveKit">How to reach LiveKit, when that is the provider.</param>
public sealed record VoiceConfig(
    string? Provider = null,
    int MaximumParticipants = 25,
    LiveKitConfig? LiveKit = null);

/// <param name="ApiUrl">
/// Where this server reaches LiveKit — a private address; clients never connect to it directly.
/// </param>
/// <param name="ApiKey">The API key LiveKit was configured with.</param>
/// <param name="ApiSecret">
/// Its secret. Better set through <c>RAINDROP_LIVEKIT_API_SECRET</c> than written into this file.
/// </param>
/// <param name="AllowWeakSecret">
/// Accept a short or well-known secret such as LiveKit's <c>--dev</c> one. Only for a throwaway
/// server on your own machine: anyone who knows the secret can mint tokens into any call.
/// </param>
public sealed record LiveKitConfig(
    string ApiUrl = "http://localhost:7880",
    string? ApiKey = null,
    string? ApiSecret = null,
    bool AllowWeakSecret = false);
