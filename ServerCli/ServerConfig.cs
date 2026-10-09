using System.Net;
using System.Reflection;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RaindropServer;
using RaindropServer.Attachments;
using RaindropServer.Channels;
using RaindropServer.Channels.Voice;
using RaindropServer.Configuration;
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
/// <remarks>
/// Loading and validating are separate on purpose. <see cref="Load"/> gets the values in, reporting
/// what it cannot make sense of; <see cref="Validate"/> then hands each section to the component
/// that owns it, because what a setting means is the component's business and not this file's. Both
/// write into one <see cref="ConfigurationReport"/>, so an owner with three mistakes is told about
/// all three at once rather than one per restart.
/// </remarks>
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

    /// <summary>Files added to messages: how large, of which types, and which types clients preview.</summary>
    public AttachmentConfig Attachments { get; init; } = new();

    /// <summary>The media servers this build can enable, besides none at all.</summary>
    private static readonly string[] KnownVoiceProviders = ["livekit"];

    /// <summary>
    /// Loads the configuration file if there is one, then lets environment variables override it.
    /// A missing file is fine — the defaults run a SQLite server on localhost.
    /// </summary>
    /// <param name="report">
    /// Collects everything the file says that this server cannot make sense of: JSON it cannot parse,
    /// settings it does not have, and values of the wrong kind. Returns the defaults for whatever it
    /// could not read, so the caller decides what to do about it rather than being handed an
    /// exception mid-startup.
    /// </param>
    public static ServerConfig Load(ConfigurationReport report) =>
        Load(Path.Combine(AppContext.BaseDirectory, FileName), report);

    /// <summary>
    /// Loads a particular configuration file, for a host that keeps its settings somewhere other than
    /// beside the executable — and for the tests that check what this makes of a mistyped one.
    /// </summary>
    /// <inheritdoc cref="Load(ConfigurationReport)" path="/param"/>
    public static ServerConfig Load(string path, ConfigurationReport report)
    {
        var name = Path.GetFileName(path);
        var config = new ServerConfig();

        if (!File.Exists(path))
        {
            report.Warn(name,
                $"was not found at {path}, so this server is running on its defaults: SQLite, localhost, and no voice.");

            return config.WithEnvironmentOverrides(report);
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Error(name, $"could not be read: {ex.Message}");
            return config;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch (JsonException ex)
        {
            report.Error(name,
                $"is not valid JSON: {ex.Message}",
                ex.LineNumber is { } line ? $"Look at line {line + 1}." : null);

            return config;
        }

        if (root is not JsonObject settings)
        {
            report.Error(name, "must hold a JSON object of settings, starting with '{'.");
            return config;
        }

        // Read the tree before handing it to the deserializer: a misspelled setting is silently
        // ignored by System.Text.Json, and a value of the wrong kind becomes an exception naming a
        // .NET type. Both are the owner's typo, and both are more useful said in their own words.
        var found = report.Problems.Count;
        ReportUnrecognised(settings, typeof(ServerConfig), report);
        var spoken = report.Problems.Count > found;

        try
        {
            // Misspelled settings are ignored here as they always were, so the rest of the file is
            // still read and still checked: an owner who has three mistakes would rather see all
            // three than be sent back for the next one after every restart.
            config = JsonSerializer.Deserialize<ServerConfig>(text, ReadOptions) ?? config;
        }
        catch (JsonException ex)
        {
            // A value of a kind the file cannot hold. Said already, and better, by the walk above.
            if (!spoken)
                report.Error(name, $"could not be read: {ex.Message}");

            return config;
        }

        return config.WithEnvironmentOverrides(report);
    }

    /// <summary>
    /// Hands each section to whichever component's settings they are, and reports what belongs to no
    /// section on its own: a provider named without the settings it needs, or two sections that
    /// contradict each other.
    /// </summary>
    /// <remarks>
    /// Everything here can be judged by reading it. What can only be found out by going and looking —
    /// whether the media server answers, whether it accepts these credentials — is an
    /// <see cref="IStartupCheck"/>, run once the services are registered.
    /// </remarks>
    public void Validate(ConfigurationReport report)
    {
        ValidateTlsFiles(report.Section("tls"));

        Apply(report, "database", ApplyTo, (DatabaseOptions options) => options.Validate(report.Section("database")));
        ValidateUsers(report);
        Apply(report, "channels", ApplyTo, (ChannelOptions options) => options.Validate(report.Section("channels")));
        Apply(report, "roles", ApplyTo, (RoleOptions options) => options.Validate(report.Section("roles")));
        Apply(report, "attachments", ApplyTo, (AttachmentOptions options) => options.Validate(report.Section("attachments")));

        ValidateVoice(report);
    }

    /// <summary>
    /// Fills one component's options from this configuration and lets the component judge them.
    /// An option that refuses the value outright — a retention window past the legal ceiling — is
    /// reported against its section rather than thrown out of startup.
    /// </summary>
    private static void Apply<T>(
        ConfigurationReport report, string section, Action<T> apply, Action<T> validate) where T : new()
    {
        var options = new T();

        try
        {
            apply(options);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            // Without the parameter name an argument exception leaves behind: the owner set a
            // setting, not a parameter, and has no way to know what 'value' was.
            var message = ex.Message.Split(" (Parameter", StringSplitOptions.None)[0];

            report.Section(section).Error("", message);
            return;
        }

        validate(options);
    }

    /// <summary>
    /// The account rules. Two of these are checked before they are applied rather than after:
    /// <see cref="UserOptions.DataRetentionAfterLeaving"/> refuses a value past the legal ceiling the
    /// moment it is set, and there is nothing useful to say about a session timeout of zero hours
    /// once it has become a <see cref="TimeSpan"/>.
    /// </summary>
    private void ValidateUsers(ConfigurationReport report)
    {
        var users = report.Section("users");
        var ceiling = (int)UserOptions.MaximumDataRetention.TotalDays;
        var valid = true;

        if (Users.DataRetentionDays < 1 || Users.DataRetentionDays > ceiling)
        {
            users.Error("dataRetentionDays",
                $"is {Users.DataRetentionDays}; a departed user's data may be kept for between 1 and {ceiling} days.",
                $"{ceiling} days is two years, the ceiling this server enforces whatever the file says.");

            valid = false;
        }

        if (Users.SessionIdleTimeoutHours <= 0)
        {
            users.Error("sessionIdleTimeoutHours",
                $"is {Users.SessionIdleTimeoutHours}; a session has to survive at least some time without a request.");

            valid = false;
        }

        if (valid)
            Apply(report, "users", ApplyTo, (UserOptions options) => options.Validate(users));
    }

    /// <summary>
    /// The voice section: that the provider is one this build has, that the settings it needs are
    /// there, and — since a media server the owner meant to enable but did not is indistinguishable
    /// from one they never wanted — that the two halves agree.
    /// </summary>
    private void ValidateVoice(ConfigurationReport report)
    {
        var voice = report.Section("voice");
        var named = Voice.Provider?.Trim();

        if (!string.IsNullOrEmpty(named) &&
            !named.Equals("none", StringComparison.OrdinalIgnoreCase) &&
            !KnownVoiceProviders.Contains(named, StringComparer.OrdinalIgnoreCase))
        {
            var suggestion = ConfigurationNames.Suggest(named, KnownVoiceProviders);

            voice.Error("provider",
                $"is '{named}', which is not a media server this build has.",
                (suggestion is null ? "" : $"Did you mean '{suggestion}'? ") +
                $"Available: {string.Join(", ", KnownVoiceProviders)}, or null for no voice.");
        }

        if (!Enum.TryParse<VoiceStartupCheck>(Voice.StartupCheck, ignoreCase: true, out _))
            voice.Error("startupCheck",
                $"is '{Voice.StartupCheck}'.",
                "Use 'off' to skip the check, 'warn' to report an unreachable media server and start anyway, or " +
                "'require' to refuse to start without one.");

        Apply(report, "voice", ApplyTo, (VoiceOptions options) => options.Validate(voice));

        if (VoiceProvider is null)
        {
            // Somebody who filled in a media server's settings meant to switch voice on, and this is
            // the one mistake that leaves no trace at runtime: calls simply cannot be joined. Said
            // only when no provider was named — a name that was named and misspelled has been
            // reported already, and saying it twice reads as two separate mistakes.
            if (string.IsNullOrEmpty(named) &&
                Voice.LiveKit is { } unused && (unused.ApiKey is not null || unused.ApiSecret is not null))
                voice.Warn("provider",
                    "is not set, so voice is off and nobody can join a call — even though voice.livekit is filled in.",
                    "Set it to 'livekit' to carry calls on the LiveKit described there.");

            return;
        }

        // Not conditional on the section being there: a provider named without one is a server that
        // cannot carry a call, and the settings it is missing say so one by one.
        Apply(report, "voice.livekit", ApplyTo,
            (LiveKitOptions options) => options.Validate(voice.Section("livekit")));
    }

    /// <summary>
    /// The files TLS rests on: that this server can read the certificate it was pointed at, that it
    /// can write the ones it is expected to generate, and that neither is readable by every account
    /// on the machine.
    /// </summary>
    private void ValidateTlsFiles(ConfigurationReport report)
    {
        if (!Tls.Enabled)
        {
            report.Error("enabled",
                "is false, but this server can no longer run without TLS: clients pin its own certificate and speak " +
                "gRPC over HTTP/2 to it, so TLS has to end here.",
                "Remove the setting (or set it to true). A reverse proxy in front of the server must pass TLS through " +
                "rather than terminate it.");
            return;
        }

        if (report.Require("certificatePath", Tls.CertificatePath, "is required when TLS is enabled."))
            ValidateKeyFile(report, "certificatePath", ResolvePath(Tls.CertificatePath), generated: true);

        if (report.Require("identityKeyPath", Tls.IdentityKeyPath,
                "is required: it is the key that lets clients follow a certificate change without losing their pin."))
            ValidateKeyFile(report, "identityKeyPath", ResolvePath(Tls.IdentityKeyPath), generated: true);
    }

    /// <summary>
    /// One file holding a secret: readable if it is there, creatable if it is not, and not left open
    /// to every account on a shared machine.
    /// </summary>
    private static void ValidateKeyFile(ConfigurationReport report, string setting, string path, bool generated)
    {
        if (File.Exists(path))
        {
            try
            {
                using var stream = File.OpenRead(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.Error(setting, $"'{path}' cannot be read: {ex.Message}");
                return;
            }

            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(path);
                const UnixFileMode shared = UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                                            UnixFileMode.OtherRead | UnixFileMode.OtherWrite;

                if ((mode & shared) != 0)
                    report.Warn(setting,
                        $"'{path}' can be read or written by accounts other than this server's.",
                        "Run `chmod 600` on it: whoever holds this file can impersonate the server to every client " +
                        "that pinned it.");
            }

            return;
        }

        if (!generated)
        {
            report.Error(setting, $"'{path}' does not exist.");
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));

        if (string.IsNullOrEmpty(directory) || Directory.Exists(directory))
            return;

        report.Error(setting,
            $"'{path}' does not exist and neither does the folder it would be generated in.",
            "Create the folder, or point the setting somewhere this server can write.");
    }

    /// <summary>
    /// The media server named by <c>voice.provider</c>, or none. Unknown names are reported by
    /// <see cref="Validate"/>, which runs before anything asks this.
    /// </summary>
    public string? VoiceProvider =>
        Voice.Provider?.Trim().ToLowerInvariant() is { Length: > 0 } provider &&
        KnownVoiceProviders.Contains(provider)
            ? provider
            : null;

    /// <summary>How hard the server tries to reach its media server before it starts listening.</summary>
    public VoiceStartupCheck VoiceStartupCheck =>
        Enum.TryParse<VoiceStartupCheck>(Voice.StartupCheck, ignoreCase: true, out var mode)
            ? mode
            : RaindropServer.Channels.Voice.VoiceStartupCheck.Warn;

    /// <summary>Applies the configured voice rules to the options the server reads.</summary>
    public void ApplyTo(VoiceOptions options)
    {
        options.MaximumParticipantsPerChannel = Voice.MaximumParticipants;
        options.MaximumCameraPublishersPerChannel = Voice.MaximumCameraPublishers;
        options.MaximumScreenSharesPerChannel = Voice.MaximumScreenShares;
        options.StartupCheck = VoiceStartupCheck;
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
        options.WipeOnStartup = Database.WipeOnStartup;
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

    /// <summary>Applies the configured attachment rules to the options the server reads.</summary>
    /// <remarks>
    /// Megabytes become bytes here, and a relative storage folder is resolved next to the executable,
    /// like the certificate. Extension lists are passed on as written, so the options' own validation
    /// can say which entry is wrong rather than having it silently corrected.
    /// </remarks>
    public void ApplyTo(AttachmentOptions options)
    {
        var attachments = Attachments;
        var preview = attachments.Preview ?? new AttachmentPreviewConfig();

        options.Enabled = attachments.Enabled;
        options.StoragePath = string.IsNullOrWhiteSpace(attachments.StoragePath) ? "" : ResolvePath(attachments.StoragePath);
        options.MaximumFileBytes = Bytes(attachments.MaximumFileSizeMegabytes);
        options.MaximumFileBytesByExtension = (attachments.MaximumFileSizeMegabytesByExtension ?? [])
            .ToDictionary(entry => entry.Key, entry => Bytes(entry.Value), StringComparer.Ordinal);
        options.AllowedExtensions = attachments.AllowedExtensions;
        options.BlockedExtensions = attachments.BlockedExtensions ?? [];
        options.AllowFilesWithoutExtension = attachments.AllowFilesWithoutExtension;
        options.MaximumFilesPerMessage = attachments.MaximumFilesPerMessage;
        options.PendingUploadExpiry = TimeSpan.FromHours(Math.Clamp(attachments.PendingUploadExpiryHours, -1e6, 1e6));
        options.MaximumPendingUploadsPerMember = attachments.MaximumPendingUploadsPerMember;
        options.PreviewExtensions = preview.Extensions ?? [];
        options.PreviewAutoLoadMaximumBytes = Bytes(preview.AutoLoadMaximumMegabytes);

        // Clamped rather than overflowing, so an absurd figure is reported as too large instead of
        // wrapping round to a negative one.
        static long Bytes(long megabytes) =>
            Math.Clamp(megabytes, long.MinValue / AttachmentOptions.Megabyte, long.MaxValue / AttachmentOptions.Megabyte) *
            AttachmentOptions.Megabyte;
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

    // ── Reading the file strictly ────────────────────────────────────────────

    /// <summary>
    /// Walks the settings the owner wrote against the settings this build has, reporting anything it
    /// does not recognise and anything whose value is of the wrong kind.
    /// </summary>
    /// <remarks>
    /// A misspelled setting is the mistake worth catching hardest: the deserializer passes over it
    /// without a word, leaving a server that runs on a default nobody chose — an
    /// <c>apiSecret</c> written as <c>apisecrets</c> switches off no feature and prints no warning,
    /// it just never reaches LiveKit.
    /// </remarks>
    private static void ReportUnrecognised(JsonObject settings, Type type, ConfigurationReport report)
    {
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var known = properties.Select(JsonName).ToList();
        var nullability = new NullabilityInfoContext();

        foreach (var (name, value) in settings)
        {
            var property = properties.FirstOrDefault(
                candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));

            if (property is null)
            {
                var suggestion = ConfigurationNames.Suggest(name, known);

                report.Error(name,
                    "is not a setting this server has, so it does nothing.",
                    suggestion is null
                        ? $"This section takes: {string.Join(", ", known)}."
                        : $"Did you mean '{suggestion}'?");

                continue;
            }

            var setting = report.Section(JsonName(property));
            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

            if (value is null)
            {
                var nullable = propertyType != property.PropertyType ||
                               (!propertyType.IsValueType &&
                                nullability.Create(property).WriteState != NullabilityState.NotNull);

                if (!nullable)
                    setting.Error("", $"is null, but it has to be {Describe(propertyType)}.");

                continue;
            }

            // A map the owner fills in — an extension to a size — rather than settings of ours: its keys
            // are theirs to choose, and only its values have a kind to check.
            if (DictionaryValueType(propertyType) is { } valueType)
            {
                if (value is not JsonObject map)
                {
                    setting.Error("", "has to be a group of names and values in { }.");
                    continue;
                }

                foreach (var (key, entry) in map)
                {
                    if (entry is null || !Matches(entry, valueType))
                        setting.Section(key).Error("", $"has to be {Describe(valueType)}, and is {(entry is null ? "null" : Written(entry))}.");
                }

                continue;
            }

            if (IsSection(propertyType))
            {
                if (value is JsonObject section)
                    ReportUnrecognised(section, propertyType, setting);
                else
                    setting.Error("", "has to be a group of settings in { }.");

                continue;
            }

            if (!Matches(value, propertyType))
                setting.Error("", $"has to be {Describe(propertyType)}, and is {Written(value)}.");
        }
    }

    /// <summary>The value type of a string-keyed dictionary setting, or null for anything else.</summary>
    private static Type? DictionaryValueType(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>) &&
        type.GetGenericArguments()[0] == typeof(string)
            ? type.GetGenericArguments()[1]
            : null;

    /// <summary>Whether this is a nested group of settings rather than one value.</summary>
    private static bool IsSection(Type type) =>
        type.IsClass && type != typeof(string) && !type.IsArray;

    /// <summary>Whether the JSON the owner wrote can become this kind of setting at all.</summary>
    private static bool Matches(JsonNode value, Type type)
    {
        if (type.IsArray)
            return value is JsonArray array && array.All(item => item is null || Matches(item, type.GetElementType()!));

        if (value is not JsonValue leaf)
            return false;

        if (type == typeof(string))
            return leaf.TryGetValue<string>(out _);

        if (type == typeof(bool))
            return leaf.TryGetValue<bool>(out _);

        if (type == typeof(int) || type == typeof(long))
            return leaf.TryGetValue<long>(out _);

        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
            return leaf.TryGetValue<double>(out _);

        return true;
    }

    private static string Describe(Type type) => type switch
    {
        _ when type.IsArray => $"a list of {Describe(type.GetElementType()!)}s in [ ]",
        _ when type == typeof(string) => "text in quotes",
        _ when type == typeof(bool) => "true or false",
        _ when type == typeof(int) || type == typeof(long) => "a whole number",
        _ when type == typeof(double) || type == typeof(float) || type == typeof(decimal) => "a number",
        _ => "a group of settings in { }"
    };

    private static string Written(JsonNode value) => value switch
    {
        JsonArray => "a list",
        JsonObject => "a group of settings",
        _ when value.GetValueKind() == JsonValueKind.String => $"text ({value.ToJsonString()})",
        _ when value.GetValueKind() is JsonValueKind.True or JsonValueKind.False => "true or false",
        _ => $"{value.ToJsonString()}"
    };

    private static string Camel(string name) =>
        name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    /// <summary>What the file calls this setting, which is not always what the property is called.</summary>
    private static string JsonName(PropertyInfo property) =>
        property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? Camel(property.Name);

    // ── Environment overrides ────────────────────────────────────────────────

    /// <summary>Every variable this server reads, and which setting each one stands in for.</summary>
    private static readonly Dictionary<string, string> KnownVariables = new(StringComparer.Ordinal)
    {
        ["RAINDROP_URLS"] = "urls",
        ["RAINDROP_DB_PROVIDER"] = "database.provider",
        ["RAINDROP_DB_CONNECTION_STRING"] = "database.connectionString",
        ["RAINDROP_DEV_WIPE_DATABASE"] = "database.wipeOnStartup",
        ["RAINDROP_TLS_ENABLED"] = "tls.enabled",
        ["RAINDROP_TLS_CERTIFICATE_PATH"] = "tls.certificatePath",
        ["RAINDROP_TLS_CERTIFICATE_PASSWORD"] = "tls.certificatePassword",
        ["RAINDROP_TLS_IDENTITY_KEY_PATH"] = "tls.identityKeyPath",
        ["RAINDROP_VOICE_PROVIDER"] = "voice.provider",
        ["RAINDROP_VOICE_STARTUP_CHECK"] = "voice.startupCheck",
        ["RAINDROP_LIVEKIT_API_URL"] = "voice.livekit.apiUrl",
        ["RAINDROP_LIVEKIT_API_KEY"] = "voice.livekit.apiKey",
        ["RAINDROP_LIVEKIT_API_SECRET"] = "voice.livekit.apiSecret",
        ["RAINDROP_LIVEKIT_ALLOW_WEAK_SECRET"] = "voice.livekit.allowWeakSecret",
        ["RAINDROP_ATTACHMENTS_ENABLED"] = "attachments.enabled",
        ["RAINDROP_ATTACHMENTS_PATH"] = "attachments.storagePath",
        ["RAINDROP_ATTACHMENTS_MAX_MB"] = "attachments.maximumFileSizeMegabytes"
    };

    private ServerConfig WithEnvironmentOverrides(ConfigurationReport report)
    {
        ReportUnknownVariables(report);

        var urls = Text("RAINDROP_URLS", report);

        return this with
        {
            Urls = urls is null
                ? Urls
                : urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Database = Database with
            {
                Provider = Text("RAINDROP_DB_PROVIDER", report) ?? Database.Provider,
                ConnectionString = Text("RAINDROP_DB_CONNECTION_STRING", report) ?? Database.ConnectionString,
                WipeOnStartup = Flag("RAINDROP_DEV_WIPE_DATABASE", report) ?? Database.WipeOnStartup
            },
            Tls = Tls with
            {
                Enabled = Flag("RAINDROP_TLS_ENABLED", report) ?? Tls.Enabled,
                CertificatePath = Text("RAINDROP_TLS_CERTIFICATE_PATH", report) ?? Tls.CertificatePath,
                CertificatePassword = Text("RAINDROP_TLS_CERTIFICATE_PASSWORD", report) ?? Tls.CertificatePassword,
                IdentityKeyPath = Text("RAINDROP_TLS_IDENTITY_KEY_PATH", report) ?? Tls.IdentityKeyPath
            },
            Voice = Voice with
            {
                Provider = Text("RAINDROP_VOICE_PROVIDER", report) ?? Voice.Provider,
                StartupCheck = Text("RAINDROP_VOICE_STARTUP_CHECK", report) ?? Voice.StartupCheck,
                LiveKit = (Voice.LiveKit ?? new LiveKitConfig()) with
                {
                    ApiUrl = Text("RAINDROP_LIVEKIT_API_URL", report)
                             ?? Voice.LiveKit?.ApiUrl ?? new LiveKitConfig().ApiUrl,
                    ApiKey = Text("RAINDROP_LIVEKIT_API_KEY", report) ?? Voice.LiveKit?.ApiKey,
                    ApiSecret = Text("RAINDROP_LIVEKIT_API_SECRET", report) ?? Voice.LiveKit?.ApiSecret,
                    AllowWeakSecret = Flag("RAINDROP_LIVEKIT_ALLOW_WEAK_SECRET", report)
                                      ?? Voice.LiveKit?.AllowWeakSecret ?? false
                }
            },
            Attachments = Attachments with
            {
                Enabled = Flag("RAINDROP_ATTACHMENTS_ENABLED", report) ?? Attachments.Enabled,
                StoragePath = Text("RAINDROP_ATTACHMENTS_PATH", report) ?? Attachments.StoragePath,
                MaximumFileSizeMegabytes = WholeNumber("RAINDROP_ATTACHMENTS_MAX_MB", report) ?? Attachments.MaximumFileSizeMegabytes
            }
        };
    }

    /// <summary>A variable holding a whole number.</summary>
    private static long? WholeNumber(string variable, ConfigurationReport report)
    {
        var value = Text(variable, report);

        if (value is null)
            return null;

        if (long.TryParse(value.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number))
            return number;

        report.Error(variable,
            $"is '{value}', which is not a whole number, so {KnownVariables[variable]} cannot be overridden.");

        return null;
    }

    /// <summary>
    /// One variable's value, or null when it is not set. A variable set to nothing at all is treated
    /// as unset — that is a compose file passing through something the host never defined — but said
    /// out loud, because the setting it was meant to override is not the one taking effect.
    /// </summary>
    private static string? Text(string variable, ConfigurationReport report)
    {
        var value = Environment.GetEnvironmentVariable(variable);

        if (value is null)
            return null;

        if (!string.IsNullOrWhiteSpace(value))
            return value;

        report.Warn(variable,
            $"is set but empty, so it does not override {KnownVariables[variable]}.",
            "A variable passed through from a host that never defined it arrives like this.");

        return null;
    }

    /// <summary>A variable holding a yes or a no, in the spellings people actually write.</summary>
    private static bool? Flag(string variable, ConfigurationReport report)
    {
        var value = Text(variable, report);

        if (value is null)
            return null;

        switch (value.Trim().ToLowerInvariant())
        {
            case "true" or "1" or "yes" or "on":
                return true;

            case "false" or "0" or "no" or "off":
                return false;

            default:
                report.Error(variable,
                    $"is '{value}', which is neither true nor false, so {KnownVariables[variable]} cannot be overridden.");

                return null;
        }
    }

    /// <summary>
    /// Warns about a <c>RAINDROP_</c> variable this build does not read. It is nearly always a
    /// misspelling of one it does, and the only sign of it otherwise is a setting that stays at its
    /// default for no visible reason.
    /// </summary>
    private static void ReportUnknownVariables(ConfigurationReport report)
    {
        foreach (var entry in Environment.GetEnvironmentVariables().Keys.OfType<string>())
        {
            if (!entry.StartsWith("RAINDROP_", StringComparison.Ordinal) ||
                KnownVariables.ContainsKey(entry) ||
                entry.StartsWith("RAINDROP_TEST_", StringComparison.Ordinal))
                continue;

            var suggestion = ConfigurationNames.Suggest(entry, KnownVariables.Keys);

            report.Warn(entry,
                "is not a variable this server reads.",
                suggestion is null ? null : $"Did you mean {suggestion}?");
        }
    }
}

/// <param name="Enabled">
/// Must be true. Kept so an older raindrop.json still reads, and refused when false: clients pin this
/// server's own certificate and speak gRPC over HTTP/2 to it, so TLS always ends here. A reverse proxy in
/// front has to pass TLS through untouched.
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
/// <param name="WipeOnStartup">
/// Development only: drop every table before the schema is (re)created, so the server starts from an
/// empty database every time. No confirmation, no undo.
/// </param>
public sealed record DatabaseConfig(
    string Provider = "sqlite",
    string ConnectionString = "Data Source=raindrop.db",
    bool AutoCreateSchema = true,
    bool WipeOnStartup = false);

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
/// permission names — ask <c>RoleService.ListPermissions</c> for the list this build understands.
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
/// <param name="MaximumCameraPublishers">
/// How many members of one call may send their camera at once — far fewer than the call holds, since
/// every camera goes to everybody else. 0 switches camera video off on this server entirely.
/// </param>
/// <param name="MaximumScreenShares">
/// How many members of one call may share a screen or a window at once. 0 switches screen sharing off
/// on this server entirely.
/// </param>
/// <param name="StartupCheck">
/// How hard the server looks for the media server before it starts listening: <c>off</c> not at all,
/// <c>warn</c> to report an unreachable one and start anyway, <c>require</c> to refuse to start
/// without it. Credentials the media server actively refuses stop the server whichever this is.
/// </param>
/// <param name="LiveKit">How to reach LiveKit, when that is the provider.</param>
public sealed record VoiceConfig(
    string? Provider = null,
    int MaximumParticipants = 25,
    int MaximumCameraPublishers = 6,
    int MaximumScreenShares = 2,
    string StartupCheck = "warn",
    // Named here as the file names it: the property is LiveKit, and a server owner told their typo
    // was in 'voice.liveKit' would go looking for a setting that does not appear anywhere.
    [property: JsonPropertyName("livekit")] LiveKitConfig? LiveKit = null);

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

/// <param name="Enabled">Whether members may add files to messages at all.</param>
/// <param name="StoragePath">
/// The folder encrypted files are kept in, relative to the executable. Back it up with the database:
/// the database says which message each file belongs to, and the folder holds the file.
/// </param>
/// <param name="MaximumFileSizeMegabytes">The largest file accepted, in megabytes (1 MB = 1,048,576 bytes).</param>
/// <param name="MaximumFileSizeMegabytesByExtension">
/// Limits for particular extensions, larger or smaller than the general one, e.g. <c>{ "mp4": 500 }</c>.
/// </param>
/// <param name="AllowedExtensions">
/// The only extensions accepted, lower case without the dot; null to accept any that is not blocked.
/// </param>
/// <param name="BlockedExtensions">Extensions never accepted, whatever <paramref name="AllowedExtensions"/> says.</param>
/// <param name="AllowFilesWithoutExtension">Whether a file whose name has no extension may be uploaded.</param>
/// <param name="MaximumFilesPerMessage">The most files one message may carry.</param>
/// <param name="PendingUploadExpiryHours">How long an upload may wait to be sent in a message before it is thrown away.</param>
/// <param name="MaximumPendingUploadsPerMember">How many uploads one member may have waiting to be sent.</param>
/// <param name="Preview">Which types clients show inline rather than as a file to save.</param>
public sealed record AttachmentConfig(
    bool Enabled = true,
    string StoragePath = "attachments",
    long MaximumFileSizeMegabytes = 25,
    Dictionary<string, long>? MaximumFileSizeMegabytesByExtension = null,
    string[]? AllowedExtensions = null,
    string[]? BlockedExtensions = null,
    bool AllowFilesWithoutExtension = true,
    int MaximumFilesPerMessage = 10,
    double PendingUploadExpiryHours = 24,
    int MaximumPendingUploadsPerMember = 20,
    AttachmentPreviewConfig? Preview = null)
{
    // Left out of the file, the blocklist is the shipped one; written as [], nothing is blocked.
    public string[]? BlockedExtensions { get; init; } = BlockedExtensions ?? [.. AttachmentOptions.DefaultBlockedExtensions];
}

/// <param name="Extensions">
/// Extensions clients show inline — images, video, audio — when they know how to render them.
/// Everything else appears as a file to save.
/// </param>
/// <param name="AutoLoadMaximumMegabytes">
/// The largest previewable file a client fetches and shows by itself; a larger one waits until the
/// member asks. 0 never loads one unasked.
/// </param>
public sealed record AttachmentPreviewConfig(string[]? Extensions = null, long AutoLoadMaximumMegabytes = 10)
{
    public string[]? Extensions { get; init; } = Extensions ?? [.. AttachmentOptions.DefaultPreviewExtensions];
}
