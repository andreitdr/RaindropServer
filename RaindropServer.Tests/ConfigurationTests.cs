using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RaindropServer.Channels;
using RaindropServer.Channels.Voice;
using RaindropServer.Configuration;
using RaindropServer.Data;
using RaindropServer.Users;
using RaindropServer.Voice.LiveKit;
using ServerCli;

namespace RaindropServer.Tests;

/// <summary>
/// The collector every component reports into: where a problem ends up named, and what a host that
/// would rather have an exception gets out of it.
/// </summary>
public class ConfigurationReportTests
{
    [Fact]
    public void A_section_names_the_settings_inside_it()
    {
        var report = new ConfigurationReport();

        report.Section("voice").Section("livekit").Error("apiSecret", "is too short.");

        Assert.Equal("voice.livekit.apiSecret", Assert.Single(report.Problems).Setting);
    }

    [Fact]
    public void Errors_are_reported_before_warnings()
    {
        var report = new ConfigurationReport();

        report.Warn("channels.allowPlaintextMessages", "is on.");
        report.Error("database.provider", "is not installed.");

        Assert.Equal(
            ["database.provider", "channels.allowPlaintextMessages"],
            report.Ordered().Select(problem => problem.Setting));

        Assert.True(report.HasErrors);
    }

    [Fact]
    public void Warnings_alone_do_not_stop_a_server()
    {
        var report = new ConfigurationReport();

        report.Warn("urls", "listens without TLS.");

        Assert.False(report.HasErrors);
        Assert.True(report.HasProblems);
        report.ThrowIfInvalid();
    }

    [Fact]
    public void Every_error_is_carried_by_the_exception()
    {
        var report = new ConfigurationReport();

        report.Error("voice.provider", "is unknown.");
        report.Error("database.provider", "is unknown.");

        var error = Assert.Throws<InvalidOperationException>(report.ThrowIfInvalid);

        Assert.Contains("voice.provider", error.Message);
        Assert.Contains("database.provider", error.Message);
    }

    [Theory]
    [InlineData("maximumParticipans", "maximumParticipants")]
    [InlineData("maximumCameraPublisher", "maximumCameraPublishers")]
    [InlineData("apisecret", "apiSecret")]
    [InlineData("livekid", "livekit")]
    public void A_near_miss_is_pointed_at_what_it_was_probably_meant_to_be(string typed, string meant) =>
        Assert.Equal(meant, ConfigurationNames.Suggest(typed,
            ["maximumParticipants", "maximumCameraPublishers", "apiSecret", "livekit"]));

    [Fact]
    public void Something_that_resembles_nothing_is_left_unguessed() =>
        Assert.Null(ConfigurationNames.Suggest("elephant", ["maximumParticipants", "apiSecret", "livekit"]));
}

/// <summary>
/// What the server makes of the file its owner wrote: the mistakes a JSON deserializer passes over
/// in silence are the ones worth catching, since each one leaves a server running on a default
/// nobody chose.
/// </summary>
public class ServerConfigTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "raindrop-config-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private (ServerConfig Config, ConfigurationReport Report) Read(string json)
    {
        var path = Path.Combine(_directory, "raindrop.json");
        File.WriteAllText(path, json);

        var report = new ConfigurationReport();
        var config = ServerConfig.Load(path, report);
        config.Validate(report);

        return (config, report);
    }

    private static ConfigurationProblem Find(ConfigurationReport report, string setting) =>
        Assert.Single(report.Problems, problem => problem.Setting == setting);

    [Fact]
    public void A_misspelled_setting_is_an_error_naming_what_it_was_meant_to_be()
    {
        var (_, report) = Read("""{ "voice": { "maxParticipants": 10 } }""");

        var problem = Find(report, "voice.maxParticipants");

        Assert.Equal(ConfigurationSeverity.Error, problem.Severity);
        Assert.Contains("maximumParticipants", problem.Remedy);
    }

    [Fact]
    public void A_misspelled_setting_does_not_stop_the_rest_of_the_file_being_checked()
    {
        var (_, report) = Read("""
            { "voice": { "maxParticipants": 10 }, "database": { "provider": "postgres!" } }
            """);

        Assert.Equal(ConfigurationSeverity.Error, Find(report, "voice.maxParticipants").Severity);
        Assert.Equal(ConfigurationSeverity.Error, Find(report, "database.provider").Severity);
    }

    [Theory]
    [InlineData("""{ "voice": { "maximumParticipants": "25" } }""", "voice.maximumParticipants", "whole number")]
    [InlineData("""{ "tls": { "enabled": "true" } }""", "tls.enabled", "true or false")]
    [InlineData("""{ "urls": "https://localhost:5001" }""", "urls", "list")]
    [InlineData("""{ "voice": { "livekit": "http://localhost:7880" } }""", "voice.livekit", "group of settings")]
    public void A_value_of_the_wrong_kind_is_reported_as_the_kind_it_should_be(string json, string setting, string expected)
    {
        var (_, report) = Read(json);

        var problem = Find(report, setting);

        Assert.Equal(ConfigurationSeverity.Error, problem.Severity);
        Assert.Contains(expected, problem.Message);
    }

    [Fact]
    public void The_screen_share_cap_is_read_from_the_file_and_reaches_the_voice_rules()
    {
        var (config, report) = Read("""{ "voice": { "maximumScreenShares": 1 } }""");

        var options = new VoiceOptions();
        config.ApplyTo(options);

        Assert.DoesNotContain(report.Problems, problem => problem.Setting.StartsWith("voice.maximumScreen"));
        Assert.Equal(1, options.MaximumScreenSharesPerChannel);
    }

    [Fact]
    public void A_misspelled_screen_share_cap_is_pointed_at_the_real_one()
    {
        var (_, report) = Read("""{ "voice": { "maximumScreenShare": 1 } }""");

        Assert.Contains("maximumScreenShares", Find(report, "voice.maximumScreenShare").Remedy);
    }

    [Fact]
    public void Json_that_does_not_parse_is_reported_with_the_line_to_look_at()
    {
        var (_, report) = Read("{\n  \"urls\": [ \"https://localhost:5001\" ,\n}");

        var problem = Find(report, "raindrop.json");

        Assert.Equal(ConfigurationSeverity.Error, problem.Severity);
        Assert.Contains("line", problem.Remedy);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_still_allowed()
    {
        var (config, report) = Read("""
            {
              // The address to listen on.
              "urls": [ "https://localhost:5001" ],
            }
            """);

        Assert.Equal(["https://localhost:5001"], config.Urls);
        Assert.DoesNotContain(report.Problems, problem => problem.Severity == ConfigurationSeverity.Error);
    }

    [Fact]
    public void A_missing_file_is_a_warning_and_the_defaults()
    {
        var report = new ConfigurationReport();

        var config = ServerConfig.Load(Path.Combine(_directory, "nothing-here.json"), report);

        Assert.False(report.HasErrors);
        Assert.True(report.HasProblems);
        Assert.Equal("sqlite", config.Database.Provider);
    }

    [Fact]
    public void A_retention_window_past_the_legal_ceiling_is_reported_rather_than_thrown()
    {
        var (_, report) = Read("""{ "users": { "dataRetentionDays": 9000 } }""");

        Assert.Contains("730", Find(report, "users.dataRetentionDays").Message);
    }

    [Fact]
    public void A_default_page_size_larger_than_the_maximum_is_a_contradiction()
    {
        var (_, report) = Read("""
            { "channels": { "defaultHistoryPageSize": 500, "maximumHistoryPageSize": 200 } }
            """);

        Assert.Equal(ConfigurationSeverity.Error, Find(report, "channels.defaultHistoryPageSize").Severity);
    }

    [Fact]
    public void An_unknown_media_server_is_an_error_naming_the_ones_this_build_has()
    {
        var (_, report) = Read("""{ "voice": { "provider": "livekid" } }""");

        var problem = Find(report, "voice.provider");

        Assert.Equal(ConfigurationSeverity.Error, problem.Severity);
        Assert.Contains("livekit", problem.Remedy);
    }

    [Fact]
    public void A_media_server_named_without_being_described_is_an_error()
    {
        var (_, report) = Read("""{ "voice": { "provider": "livekit" } }""");

        Assert.Equal(ConfigurationSeverity.Error, Find(report, "voice.livekit.apiKey").Severity);
        Assert.Equal(ConfigurationSeverity.Error, Find(report, "voice.livekit.apiSecret").Severity);
    }

    /// <summary>
    /// The mistake that leaves no trace at runtime: everything LiveKit needs is there, nothing says
    /// to use it, and voice is simply off.
    /// </summary>
    [Fact]
    public void A_media_server_described_but_not_enabled_is_a_warning()
    {
        var (_, report) = Read("""
            { "voice": { "livekit": { "apiKey": "raindrop", "apiSecret": "0123456789abcdef0123456789abcdef" } } }
            """);

        Assert.Equal(ConfigurationSeverity.Warning, Find(report, "voice.provider").Severity);
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void A_startup_check_mode_this_server_does_not_have_is_an_error()
    {
        var (_, report) = Read("""{ "voice": { "startupCheck": "maybe" } }""");

        Assert.Contains("require", Find(report, "voice.startupCheck").Remedy);
    }

    [Fact]
    public void Turning_tls_off_is_refused_because_clients_only_speak_grpc_over_pinned_tls()
    {
        var (_, report) = Read("""{ "tls": { "enabled": false } }""");

        Assert.Equal(ConfigurationSeverity.Error, Find(report, "tls.enabled").Severity);
    }

    [Fact]
    public void Listening_without_tls_is_refused()
    {
        var report = new ConfigurationReport();
        new ServerHostOptions { Urls = ["http://0.0.0.0:5000"] }.Validate(report);

        Assert.Equal(ConfigurationSeverity.Error, Find(report, "urls").Severity);
        Assert.Equal(ConfigurationSeverity.Error, Find(report, "tls.certificatePath").Severity);
    }

    [Fact]
    public void A_working_configuration_is_reported_clean()
    {
        var (config, report) = Read($$"""
            {
              "urls": [ "https://localhost:5001" ],
              "database": { "provider": "sqlite", "connectionString": "Data Source=raindrop.db" },
              "voice": {
                "provider": "livekit",
                "maximumParticipants": 25,
                "startupCheck": "off",
                "livekit": {
                  "apiUrl": "http://localhost:7880",
                  "apiKey": "raindrop",
                  "apiSecret": "{{new string('k', 8) + Guid.NewGuid().ToString("N")}}"
                }
              }
            }
            """);

        Assert.Equal("livekit", config.VoiceProvider);
        Assert.Equal(VoiceStartupCheck.Off, config.VoiceStartupCheck);
        Assert.False(report.HasErrors, report.Describe());
    }
}

/// <summary>
/// The rules every component keeps for itself — what a server owner is told before anything is
/// registered, let alone connected to.
/// </summary>
public class ComponentOptionsTests
{
    private static ConfigurationReport Check(Action<ConfigurationReport> validate)
    {
        var report = new ConfigurationReport();
        validate(report);
        return report;
    }

    [Fact]
    public void A_call_of_one_is_not_a_call() =>
        Assert.True(Check(new VoiceOptions { MaximumParticipantsPerChannel = 1 }.Validate).HasErrors);

    [Fact]
    public void A_very_large_call_is_allowed_but_said_out_loud()
    {
        var report = Check(new VoiceOptions { MaximumParticipantsPerChannel = 200 }.Validate);

        Assert.False(report.HasErrors);
        Assert.True(report.HasProblems);
    }

    /// <summary>Zero is the documented way to switch camera video off, so it has to be in range.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(6, false)]
    [InlineData(-1, true)]
    [InlineData(51, true)]
    public void The_camera_cap_takes_zero_but_not_a_negative_or_an_absurd_number(int cap, bool error) =>
        Assert.Equal(error,
            Check(new VoiceOptions { MaximumCameraPublishersPerChannel = cap }.Validate).HasErrors);

    [Fact]
    public void A_camera_cap_larger_than_the_call_can_never_be_reached_and_is_said_out_loud()
    {
        var report = Check(new VoiceOptions
        {
            MaximumParticipantsPerChannel = 4,
            MaximumCameraPublishersPerChannel = 10
        }.Validate);

        Assert.False(report.HasErrors);
        Assert.Contains(report.Problems, problem => problem.Setting == "maximumCameraPublishers");
    }

    [Fact]
    public void A_great_many_cameras_at_once_is_allowed_but_said_out_loud()
    {
        var report = Check(new VoiceOptions { MaximumCameraPublishersPerChannel = 20 }.Validate);

        Assert.False(report.HasErrors);
        Assert.Contains(report.Problems, problem => problem.Setting == "maximumCameraPublishers");
    }

    /// <summary>Zero is the documented way to switch screen sharing off, so it has to be in range.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(-1, true)]
    [InlineData(11, true)]
    public void The_screen_share_cap_takes_zero_but_not_a_negative_or_an_absurd_number(int cap, bool error) =>
        Assert.Equal(error,
            Check(new VoiceOptions { MaximumScreenSharesPerChannel = cap }.Validate).HasErrors);

    [Fact]
    public void A_screen_share_cap_larger_than_the_call_can_never_be_reached_and_is_said_out_loud()
    {
        var report = Check(new VoiceOptions
        {
            MaximumParticipantsPerChannel = 2,
            MaximumCameraPublishersPerChannel = 2,
            MaximumScreenSharesPerChannel = 3
        }.Validate);

        Assert.False(report.HasErrors);
        Assert.Contains(report.Problems, problem => problem.Setting == "maximumScreenShares");
    }

    [Fact]
    public void Several_screen_shares_at_once_are_allowed_but_said_out_loud()
    {
        var report = Check(new VoiceOptions { MaximumScreenSharesPerChannel = 5 }.Validate);

        Assert.False(report.HasErrors);
        Assert.Contains(report.Problems, problem => problem.Setting == "maximumScreenShares");
    }

    [Fact]
    public void The_default_screen_share_cap_says_nothing()
    {
        var report = Check(new VoiceOptions().Validate);

        Assert.DoesNotContain(report.Problems, problem => problem.Setting == "maximumScreenShares");
    }

    [Fact]
    public void A_reconcile_pass_slower_than_the_join_grace_is_a_grace_that_does_not_mean_what_it_says()
    {
        var report = Check(new VoiceOptions
        {
            JoinGrace = TimeSpan.FromSeconds(10),
            ReconcileInterval = TimeSpan.FromSeconds(30)
        }.Validate);

        Assert.Contains(report.Problems, problem => problem.Setting == "reconcileInterval");
    }

    [Fact]
    public void Plaintext_messages_are_allowed_but_said_out_loud()
    {
        var report = Check(new ChannelOptions { AllowPlaintextMessages = true }.Validate);

        Assert.False(report.HasErrors);
        Assert.Contains(report.Problems, problem => problem.Setting == "allowPlaintextMessages");
    }

    [Fact]
    public void A_password_no_password_could_satisfy_is_an_error()
    {
        var report = Check(new UserOptions { MinimumPasswordLength = 300, MaximumPasswordLength = 256 }.Validate);

        Assert.True(report.HasErrors);
    }

    [Fact]
    public void Wiping_the_database_on_startup_is_allowed_but_said_out_loud()
    {
        var report = Check(new DatabaseOptions
        {
            Provider = "sqlite",
            ConnectionString = "Data Source=test.db",
            WipeOnStartup = true
        }.Validate);

        Assert.False(report.HasErrors);
        Assert.Contains(report.Problems, problem => problem.Setting == "wipeOnStartup");
    }
}

/// <summary>
/// The LiveKit module's own reading of its settings. Every one of these is a mistake that leaves a
/// server which starts, runs, and cannot carry a single call.
/// </summary>
public class LiveKitConfigurationTests
{
    private static readonly string Strong = new string('s', 20) + "aBcDeFgH1234";

    private static ConfigurationReport Check(LiveKitOptions options)
    {
        var report = new ConfigurationReport();
        options.Validate(report);
        return report;
    }

    private static LiveKitOptions Options(string? url = null, string? key = null, string? secret = null) => new()
    {
        ApiUrl = url ?? "http://localhost:7880",
        ApiKey = key ?? "raindrop",
        ApiSecret = secret ?? Strong
    };

    [Theory]
    [InlineData("http://localhost:7880")]
    [InlineData("http://127.0.0.1:7880")]
    [InlineData("http://livekit:7880")]
    [InlineData("https://livekit.example.com:7880")]
    public void Private_or_encrypted_api_urls_are_accepted(string url) =>
        Assert.False(Check(Options(url)).HasErrors);

    [Theory]
    [InlineData("http://livekit.example.com:7880")]
    [InlineData("http://192.168.1.5:7880")]
    public void Plain_http_across_a_network_is_refused(string url) =>
        Assert.True(Check(Options(url)).HasErrors);

    /// <summary>The address LiveKit's own documentation shows clients, copied into the wrong setting.</summary>
    [Fact]
    public void The_websocket_address_is_refused_by_name()
    {
        var problem = Assert.Single(Check(Options("ws://localhost:7880")).Problems);

        Assert.Contains("WebSocket", problem.Message);
    }

    [Theory]
    [InlineData("http://localhost:7880/twirp")]
    [InlineData("http://localhost:7880/rtc")]
    public void An_api_url_with_a_path_is_refused(string url) =>
        Assert.True(Check(Options(url)).HasErrors);

    [Fact]
    public void An_api_url_without_a_port_is_a_warning_naming_the_one_livekit_uses()
    {
        var report = Check(Options("https://livekit.example.com"));

        Assert.False(report.HasErrors);
        Assert.Contains("7880", Assert.Single(report.Problems).Remedy);
    }

    [Theory]
    [InlineData("secret")]
    [InlineData("devkey")]
    [InlineData("too-short")]
    public void Weak_secrets_are_refused_unless_explicitly_allowed(string secret)
    {
        Assert.True(Check(Options(secret: secret)).HasErrors);

        var allowed = Options(secret: secret);
        allowed.AllowWeakSecret = true;

        var report = Check(allowed);

        Assert.False(report.HasErrors);
        Assert.True(report.HasProblems);
    }

    /// <summary>Long enough to pass a length check, and still trivial to guess.</summary>
    [Fact]
    public void A_long_secret_of_almost_no_distinct_characters_is_refused() =>
        Assert.True(Check(Options(secret: new string('a', 48))).HasErrors);

    [Theory]
    [InlineData("${LIVEKIT_API_SECRET}")]
    [InlineData("<your secret goes here, 32+ chars>")]
    [InlineData("change-me-to-a-real-secret-please")]
    public void A_placeholder_left_in_the_file_is_refused(string secret) =>
        Assert.True(Check(Options(secret: secret)).HasErrors);

    /// <summary>What an <c>.env</c> line's quotes do when they are read as part of the value.</summary>
    [Fact]
    public void A_quoted_secret_is_refused()
    {
        var problem = Assert.Single(Check(Options(secret: $"\"{Strong}\"")).Problems);

        Assert.Equal(ConfigurationSeverity.Error, problem.Severity);
        Assert.Contains("quoted", problem.Message);
    }

    [Fact]
    public void A_secret_with_whitespace_around_it_is_a_warning()
    {
        var report = Check(Options(secret: Strong + "\n"));

        Assert.False(report.HasErrors);
        Assert.True(report.HasProblems);
    }

    [Fact]
    public void A_key_that_is_also_the_secret_is_refused() =>
        Assert.True(Check(Options(key: Strong, secret: Strong)).HasErrors);

    [Fact]
    public void A_missing_key_or_secret_is_refused()
    {
        Assert.True(Check(Options(key: "")).HasErrors);
        Assert.True(Check(Options(secret: "")).HasErrors);
    }

    [Fact]
    public void A_token_that_outlives_the_member_it_was_minted_for_is_refused()
    {
        var options = Options();
        options.TokenLifetime = TimeSpan.FromHours(12);

        Assert.True(Check(options).HasErrors);
    }

    [Fact]
    public void Registering_the_module_still_refuses_settings_it_cannot_work_with() =>
        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddRaindropLiveKit(options => options.ApiSecret = "secret"));
}

/// <summary>
/// Going and looking, against a LiveKit that answers however the test says. These are the failures
/// no amount of reading the configuration could have found.
/// </summary>
public class LiveKitStartupCheckTests
{
    /// <summary>A LiveKit that answers every call the same way, or one that is not there at all.</summary>
    private sealed class StubLiveKit : IDisposable
    {
        private readonly HttpListener? _listener;

        private StubLiveKit(string url) => Url = url;

        public StubLiveKit(HttpStatusCode status, string body, DateTimeOffset? clock = null)
        {
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}";
            _listener = new HttpListener();
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();

            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception)
                    {
                        return;
                    }

                    // Twirp answers in the request's encoding — protobuf — and refuses in JSON, always.
                    context.Response.StatusCode = (int)status;
                    context.Response.ContentType = status == HttpStatusCode.OK ? "application/protobuf" : "application/json";

                    if (clock is { } time)
                        context.Response.Headers["Date"] = time.ToString("R");

                    var bytes = Encoding.UTF8.GetBytes(body);
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            });
        }

        /// <summary>An address nothing is listening on, for the sidecar that has not started yet.</summary>
        public static StubLiveKit Absent() => new($"http://127.0.0.1:{FreePort()}");

        public string Url { get; }

        public void Dispose()
        {
            _listener?.Stop();
            _listener?.Close();
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }

    private static async Task<ConfigurationReport> CheckAsync(
        StubLiveKit livekit, VoiceStartupCheck mode = VoiceStartupCheck.Warn)
    {
        var options = new LiveKitOptions
        {
            ApiUrl = livekit.Url,
            ApiKey = "raindrop",
            ApiSecret = new string('s', 20) + "aBcDeFgH1234",
            RequestTimeout = TimeSpan.FromSeconds(2)
        };

        using var rooms = new LiveKitRoomClient(options, new LiveKitJwt(options));
        var report = new ConfigurationReport();

        await new LiveKitStartupCheck(options, new VoiceOptions { StartupCheck = mode }, rooms)
            .CheckAsync(report, default);

        return report;
    }

    [Fact]
    public async Task A_livekit_that_answers_is_nothing_to_report()
    {
        using var livekit = new StubLiveKit(HttpStatusCode.OK, "" /* an empty ListRoomsResponse */);

        Assert.False((await CheckAsync(livekit)).HasProblems);
    }

    /// <summary>
    /// The secret that does not match the one LiveKit was given. Nothing in the file is malformed,
    /// and no call will ever connect.
    /// </summary>
    [Fact]
    public async Task A_refused_credential_is_an_error_however_patient_the_owner_asked_to_be()
    {
        using var livekit = new StubLiveKit(HttpStatusCode.Unauthorized,
            """{"code":"unauthenticated","msg":"invalid authorization token"}""");

        foreach (var mode in new[] { VoiceStartupCheck.Warn, VoiceStartupCheck.Require })
        {
            var report = await CheckAsync(livekit, mode);

            Assert.True(report.HasErrors);
            Assert.Equal("voice.livekit.apiKey", report.Problems[0].Setting);
        }
    }

    [Fact]
    public async Task Something_else_listening_on_the_port_is_an_error()
    {
        using var livekit = new StubLiveKit(HttpStatusCode.NotFound, "<html>not livekit</html>");

        var report = await CheckAsync(livekit);

        Assert.True(report.HasErrors);
        Assert.Equal("voice.livekit.apiUrl", report.Problems[0].Setting);
    }

    /// <summary>
    /// A media server that has not finished starting is the ordinary case, and not a reason to
    /// refuse to run a server — unless its owner said it was.
    /// </summary>
    [Fact]
    public async Task An_unreachable_livekit_is_as_serious_as_the_owner_asked_for()
    {
        using var livekit = StubLiveKit.Absent();

        var warned = await CheckAsync(livekit);

        Assert.False(warned.HasErrors);
        Assert.True(warned.HasProblems);

        Assert.True((await CheckAsync(livekit, VoiceStartupCheck.Require)).HasErrors);
    }

    [Fact]
    public async Task Nothing_is_asked_when_the_check_is_switched_off()
    {
        using var livekit = StubLiveKit.Absent();

        Assert.False((await CheckAsync(livekit, VoiceStartupCheck.Off)).HasProblems);
    }

    /// <summary>
    /// Tokens carry the time they were minted, so two machines that disagree about it make tokens
    /// that expire before they are used.
    /// </summary>
    [Fact]
    public async Task A_livekit_whose_clock_is_far_from_this_one_is_a_warning()
    {
        using var livekit = new StubLiveKit(HttpStatusCode.OK, "" /* an empty ListRoomsResponse */,
            clock: DateTimeOffset.UtcNow.AddMinutes(10));

        var report = await CheckAsync(livekit);

        Assert.False(report.HasErrors);
        Assert.Contains("clock", Assert.Single(report.Problems).Message);
    }
}

