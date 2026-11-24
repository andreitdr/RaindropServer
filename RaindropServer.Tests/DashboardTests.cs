using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using RaindropServer.Configuration;
using RaindropServer.Dashboard;
using RaindropServer.Dashboard.Metrics;
using RaindropServer.E2ee;
using RaindropServer.Grpc;
using RaindropServer.Users;
using Proto = RaindropServer.Grpc.Protocol;

namespace RaindropServer.Tests;

/// <summary>Who may reach the dashboard's port at all.</summary>
public class NetworkAllowlistTests
{
    private static readonly NetworkAllowlist Private = NetworkAllowlist.Parse(DashboardOptions.DefaultAllowedNetworks, out _);

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("10.255.0.4")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.20")]
    [InlineData("fd12:3456::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:192.168.1.20")] // IPv4 as a dual-stack socket reports it
    public void The_private_ranges_are_let_in(string address) =>
        Assert.True(Private.Contains(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("192.169.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:8.8.8.8")]
    public void Everything_else_is_not(string address) =>
        Assert.False(Private.Contains(IPAddress.Parse(address)));

    [Fact]
    public void Entries_that_are_not_networks_are_collected_rather_than_dropped_silently()
    {
        var allowlist = NetworkAllowlist.Parse(["10.0.0.0/8", "10.0.0.1/8", "lan", "192.168.1.0/24"], out var invalid);

        Assert.Equal(["10.0.0.1/8", "lan"], invalid);
        Assert.Equal(2, allowlist.Networks.Count);
    }
}

/// <summary>The dashboard's settings and the extra listener they give the host.</summary>
public class DashboardOptionsTests
{
    private static ConfigurationReport Validate(DashboardOptions options)
    {
        var report = new ConfigurationReport();
        options.Validate(report.Section("dashboard"));
        return report;
    }

    [Fact]
    public void The_defaults_are_valid()
    {
        Assert.False(Validate(new DashboardOptions { Enabled = true }).HasProblems);
    }

    [Theory]
    [InlineData("http://0.0.0.0:5002")]
    [InlineData("0.0.0.0:5002")]
    public void It_is_never_served_without_tls(string url)
    {
        var report = Validate(new DashboardOptions { Enabled = true, Url = url });

        Assert.Equal(ConfigurationSeverity.Error, Assert.Single(report.Problems, p => p.Setting == "dashboard.url").Severity);
    }

    [Fact]
    public void A_network_that_is_not_one_is_an_error_and_any_address_is_a_warning()
    {
        var report = Validate(new DashboardOptions { Enabled = true, AllowedNetworks = ["192.168.1.1/24", "0.0.0.0/0"] });

        var problems = report.Problems.Where(p => p.Setting == "dashboard.allowedNetworks").ToList();
        Assert.Contains(problems, p => p.Severity == ConfigurationSeverity.Error && p.Message.Contains("192.168.1.1/24"));
        Assert.Contains(problems, p => p.Severity == ConfigurationSeverity.Warning && p.Message.Contains("any address"));
    }

    [Fact]
    public void Turned_off_it_is_not_judged()
    {
        Assert.False(Validate(new DashboardOptions { Enabled = false, Url = "nonsense", AllowedNetworks = [] }).HasProblems);
    }

    [Fact]
    public void Its_listener_cannot_share_the_port_clients_use()
    {
        var host = new ServerHostOptions { Urls = ["https://0.0.0.0:5001"] };
        host.Listeners.Add(new DashboardOptions { Url = "https://0.0.0.0:5001" }.Listener);

        var report = new ConfigurationReport();
        host.Validate(report);

        Assert.Contains(report.Problems, p => p.Setting == "dashboard.url" && p.Severity == ConfigurationSeverity.Error);
    }

    [Fact]
    public void Its_listener_must_be_https()
    {
        var host = new ServerHostOptions { Urls = ["https://0.0.0.0:5001"] };
        host.Listeners.Add(new ServerListener("dashboard", "http://0.0.0.0:5002", _ => true));

        var report = new ConfigurationReport();
        host.Validate(report);

        Assert.Contains(report.Problems, p => p.Setting == "dashboard.url" && p.Severity == ConfigurationSeverity.Error);
    }
}

/// <summary>The dashboard's one account: first setup with the printed code, sign-in, sessions, reset.</summary>
public sealed class DashboardAuthTests : IDisposable
{
    private readonly string _folder =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"raindrop-dashboard-{Guid.NewGuid():N}")).FullName;

    private readonly List<string> _audit = [];
    private readonly AuthClock _clock = new();

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string AccountFile => Path.Combine(_folder, "dashboard-account.json");

    private DashboardAuth Create() => new(
        new DashboardOptions { Enabled = true, CredentialsPath = AccountFile, Audit = _audit.Add },
        new Pbkdf2PasswordHasher(1_000),
        _clock);

    private static string Refusal(Action call) => Assert.Throws<ApiException>(call).Code;

    [Fact]
    public void Setup_needs_the_printed_code_and_then_cannot_happen_again()
    {
        var auth = Create();
        var code = Assert.IsType<string>(auth.PendingSetupCode);
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$", code);
        Assert.False(auth.IsSetUp);

        Assert.Equal("invalid_setup_code", Refusal(() => auth.SetUp("AAAA-AAAA-AAAA", "owner", "correct horse battery")));
        Assert.Equal("weak_password", Refusal(() => auth.SetUp(code, "owner", "short")));
        Assert.Equal("invalid_username", Refusal(() => auth.SetUp(code, "  ", "correct horse battery")));

        // Forgiving of case and spacing, as a code read off a log and typed would be.
        var session = auth.SetUp($" {code.ToLowerInvariant().Replace("-", " ")} ", "owner", "correct horse battery");
        Assert.Equal("owner", session.Username);
        Assert.True(auth.IsSetUp);
        Assert.Null(auth.PendingSetupCode);
        Assert.Equal("already_set_up", Refusal(() => auth.SetUp(code, "intruder", "correct horse battery")));

        // The file keeps a hash, never the password, and only this account may read it.
        var file = File.ReadAllText(AccountFile);
        Assert.DoesNotContain("correct horse battery", file);
        Assert.Contains("pbkdf2-sha256$", file);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(AccountFile));

        // Never the password or the code in the trail.
        Assert.DoesNotContain(_audit, line => line.Contains("correct horse") || line.Contains(code));
    }

    [Fact]
    public void Sign_in_refuses_a_wrong_name_and_a_wrong_password_alike_and_survives_a_restart()
    {
        var first = Create();
        first.SetUp(first.PendingSetupCode, "Owner", "correct horse battery");

        // A restarted server reads the account back, and makes no setup code.
        var auth = Create();
        Assert.True(auth.IsSetUp);
        Assert.Null(auth.PendingSetupCode);

        Assert.Equal("invalid_credentials", Refusal(() => auth.SignIn("owner", "wrong password!!")));
        Assert.Equal("invalid_credentials", Refusal(() => auth.SignIn("someone", "correct horse battery")));

        var session = auth.SignIn(" owner ", "correct horse battery");
        Assert.True(auth.TryValidate(session.Token, out _));
        Assert.False(first.TryValidate(session.Token, out _));

        auth.SignOut(session.Token);
        Assert.False(auth.TryValidate(session.Token, out _));
    }

    [Fact]
    public void A_session_ends_when_idle_and_after_its_lifetime_however_busy()
    {
        var auth = Create();
        var session = auth.SetUp(auth.PendingSetupCode, "owner", "correct horse battery");

        // Kept alive by use for most of a day…
        for (var i = 0; i < 24; i++)
        {
            _clock.Now += TimeSpan.FromMinutes(29);
            Assert.True(auth.TryValidate(session.Token, out _), $"used after {i + 1} half-hours");
        }

        // …but not past twelve hours.
        _clock.Now += TimeSpan.FromMinutes(29);
        Assert.False(auth.TryValidate(session.Token, out _));

        var idle = auth.SignIn("owner", "correct horse battery");
        _clock.Now += TimeSpan.FromMinutes(31);
        Assert.False(auth.TryValidate(idle.Token, out _));
    }

    [Fact]
    public void Changing_the_password_signs_out_every_other_browser()
    {
        var auth = Create();
        var here = auth.SetUp(auth.PendingSetupCode, "owner", "correct horse battery");
        var there = auth.SignIn("owner", "correct horse battery");

        Assert.Equal("invalid_credentials", Refusal(() => auth.ChangePassword(here, "not it", "a new long password")));
        auth.ChangePassword(here, "correct horse battery", "a new long password");

        Assert.True(auth.TryValidate(here.Token, out _));
        Assert.False(auth.TryValidate(there.Token, out _));
        Assert.Equal("invalid_credentials", Refusal(() => auth.SignIn("owner", "correct horse battery")));
        auth.SignIn("owner", "a new long password");
    }

    [Fact]
    public void A_reset_forgets_the_account_signs_everyone_out_and_makes_a_new_code()
    {
        var auth = Create();
        var session = auth.SetUp(auth.PendingSetupCode, "owner", "correct horse battery");

        var code = auth.Reset();

        Assert.False(File.Exists(AccountFile));
        Assert.False(auth.IsSetUp);
        Assert.Equal(code, auth.PendingSetupCode);
        Assert.False(auth.TryValidate(session.Token, out _));
        auth.SetUp(code, "owner", "another long password");
    }

    [Fact]
    public void An_account_file_that_is_not_one_is_reported_rather_than_replaced()
    {
        File.WriteAllText(AccountFile, "{ not json");

        Assert.Throws<InvalidOperationException>(() => Create().Load());
        Assert.True(File.Exists(AccountFile));
    }
}

/// <summary>The Linux readers, against files written the way the kernel writes them.</summary>
public sealed class DashboardMetricsTests : IDisposable
{
    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"raindrop-metrics-{Guid.NewGuid():N}")).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string path, string text)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    [Fact]
    public void The_machine_is_read_from_proc()
    {
        Write("proc/stat", "cpu  100 20 30 800 50 0 0 0 0 0\ncpu0 50 10 15 400 25 0 0 0 0 0\n");
        Write("proc/meminfo", "MemTotal:       16000000 kB\nMemFree:         2000000 kB\nMemAvailable:    6000000 kB\n");
        Write("proc/loadavg", "0.52 0.48 0.40 2/345 6789\n");
        Write("proc/uptime", "12345.67 98765.43\n");
        Write("proc/cpuinfo", "processor\t: 0\nmodel name\t: AMD EPYC 7763 64-Core Processor\n");
        Write("etc/os-release", "NAME=\"Debian GNU/Linux\"\nPRETTY_NAME=\"Debian GNU/Linux 12 (bookworm)\"\n");
        Write("proc/net/dev",
            "Inter-|   Receive                            |  Transmit\n" +
            " face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed\n" +
            "    lo:  5000      10    0    0    0     0          0         0     5000      10    0    0    0     0       0          0\n" +
            "  eth0: 123456     100    0    0    0     0          0         0    654321     200    0    0    0     0       0          0\n");

        var proc = Path.Combine(_root, "proc");

        Assert.Equal((150UL, 1000UL), LinuxProc.ReadCpuTimes(proc));
        Assert.Equal((16_000_000L * 1024, 6_000_000L * 1024), LinuxProc.ReadMemory(proc));
        Assert.Equal([0.52, 0.48, 0.40], Assert.IsType<double[]>(LinuxProc.ReadLoadAverage(proc)));
        Assert.Equal(12345.67, LinuxProc.ReadUptimeSeconds(proc));
        Assert.Equal("AMD EPYC 7763 64-Core Processor", LinuxProc.ReadCpuModel(proc));
        Assert.Equal("Debian GNU/Linux 12 (bookworm)", LinuxProc.ReadOsRelease(Path.Combine(_root, "etc")));
        Assert.Equal((123456L, 654321L), LinuxProc.ReadNetwork(proc));

        var host = new LinuxHostMetrics(proc, Path.Combine(_root, "etc"));
        Assert.Null(host.SampleCpuPercent());
        Write("proc/stat", "cpu  200 20 80 1650 50 0 0 0 0 0\n");
        Assert.Equal(15.0, host.SampleCpuPercent()!.Value, precision: 6); // 150 busy of 1000
    }

    [Fact]
    public void A_cgroup_v2_container_reports_its_own_limits()
    {
        Write("sys/fs/cgroup/cgroup.controllers", "cpu memory pids\n");
        Write("sys/fs/cgroup/cpu.max", "150000 100000\n");
        Write("sys/fs/cgroup/cpu.stat", "usage_usec 5000000\nuser_usec 4000000\nsystem_usec 1000000\n");
        Write("sys/fs/cgroup/memory.current", "268435456\n");
        Write("sys/fs/cgroup/memory.max", "1073741824\n");
        Write("sys/fs/cgroup/pids.current", "23\n");
        Write("sys/fs/cgroup/pids.max", "max\n");

        var container = new ContainerMetrics(ContainerDetector.Docker, _root);

        Assert.Equal(1.5, container.ReadCpuLimitCores());
        Assert.Equal(5_000_000L, container.ReadCpuUsageMicroseconds());
        Assert.Equal((268435456L, 1073741824L), container.ReadMemory());
        Assert.Equal((23L, (long?)null), container.ReadProcesses());

        // "max" is no limit.
        Write("sys/fs/cgroup/cpu.max", "max 100000\n");
        Write("sys/fs/cgroup/memory.max", "max\n");
        Assert.Null(container.ReadCpuLimitCores());
        Assert.Null(container.ReadMemory().Limit);
    }

    [Fact]
    public void A_cgroup_v1_container_reports_its_own_limits()
    {
        Write("sys/fs/cgroup/cpu/cpu.cfs_quota_us", "200000\n");
        Write("sys/fs/cgroup/cpu/cpu.cfs_period_us", "100000\n");
        Write("sys/fs/cgroup/cpuacct/cpuacct.usage", "7000000000\n");
        Write("sys/fs/cgroup/memory/memory.usage_in_bytes", "104857600\n");
        Write("sys/fs/cgroup/memory/memory.limit_in_bytes", "9223372036854771712\n");

        var container = new ContainerMetrics(ContainerDetector.Docker, _root);

        Assert.Equal(2.0, container.ReadCpuLimitCores());
        Assert.Equal(7_000_000L, container.ReadCpuUsageMicroseconds());
        Assert.Equal((104857600L, (long?)null), container.ReadMemory()); // v1's "no limit" is a huge number
    }

    [Fact]
    public void Docker_is_recognised_and_a_machine_that_merely_runs_docker_is_not()
    {
        const string id = "4b825dc642cb6eb9a060e54bf8d69288fbee4904a1b2c3d4e5f60718293a4b5c";
        string? Env(string _) => null;

        // A host running Docker: its mount table names /var/lib/docker, which is not a container.
        Write("proc/self/cgroup", "0::/user.slice/user-1000.slice/session-1.scope\n");
        Write("proc/self/mountinfo", $"300 29 0:50 / /var/lib/docker/overlay2/{id}/merged rw - overlay overlay rw\n");
        Assert.Null(ContainerDetector.Detect(_root, Env));

        // .NET's images say they are a container even where Docker leaves no trace.
        Assert.Equal(ContainerDetector.Other, ContainerDetector.Detect(_root, _ => "true"));

        // Inside Docker: cgroup v1 names it, and /.dockerenv is always there.
        Write("proc/self/cgroup", $"12:memory:/docker/{id}\n");
        Assert.Equal(ContainerDetector.Docker, ContainerDetector.Detect(_root, Env));

        Write("proc/self/cgroup", "0::/\n");
        Write(".dockerenv", "");
        Assert.Equal(ContainerDetector.Docker, ContainerDetector.Detect(_root, Env));

        // Its id, from the mount of its own /etc/hostname.
        Write("proc/self/mountinfo",
            $"700 690 254:1 /docker/containers/{id}/hostname /etc/hostname rw,relatime - ext4 /dev/vda1 rw\n");
        Assert.Equal(id, ContainerDetector.ContainerId(_root));
    }
}

/// <summary>
/// The dashboard served for real — the host's own <see cref="ServerHost.Build"/>, gRPC beside it, and a
/// SQLite database — with the listener's port simulated: TestServer has no ports, so a startup filter
/// sets the local port a request arrived on from a test header.
/// </summary>
public sealed class DashboardServer : IAsyncDisposable
{
    public const int Port = 5002;
    private const string PortHeader = "X-Test-Local-Port";

    private readonly WebApplication _app;
    private readonly string _folder;

    private DashboardServer(WebApplication app, string folder)
    {
        _app = app;
        _folder = folder;
        Channel = GrpcChannel.ForAddress(app.GetTestServer().BaseAddress,
            new GrpcChannelOptions { HttpHandler = app.GetTestServer().CreateHandler() });
    }

    public GrpcChannel Channel { get; }

    public IServiceProvider Services => _app.Services;

    public string SetupCode => Services.GetRequiredService<DashboardAuth>().PendingSetupCode!;

    public static async Task<DashboardServer> StartAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"raindrop-dashboard-http-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);

        var services = new ServiceCollection();
        services.AddRaindropServer(configureAttachments: attachments => attachments.StoragePath = Path.Combine(folder, "attachments"));
        services.AddRaindropGrpc();
        services.AddRaindropDatabase(database =>
        {
            database.Provider = "sqlite";
            database.ConnectionString = $"Data Source={Path.Combine(folder, "raindrop.db")}";
        });
        await services.InitializeRaindropDatabaseAsync();

        var dashboard = new DashboardOptions
        {
            Enabled = true,
            Url = $"https://0.0.0.0:{Port}",
            CredentialsPath = Path.Combine(folder, "dashboard-account.json"),
            Audit = _ => { }
        };
        services.AddRaindropDashboard(dashboard);
        services.AddSingleton<IStartupFilter, LocalPortFromHeader>();

        var host = new ServerHostOptions();
        host.Listeners.Add(dashboard.Listener);

        var app = ServerHost.Build(services, host, builder => builder.WebHost.UseTestServer());
        await app.StartAsync();
        return new DashboardServer(app, folder);
    }

    /// <summary>A browser on the dashboard's port, sending its origin as a browser would.</summary>
    public Browser OnDashboardPort() => new(_app.GetTestServer().CreateClient(), Port);

    /// <summary>The same, on the port clients use.</summary>
    public Browser OnClientPort() => new(_app.GetTestServer().CreateClient(), null);

    public async Task<(string Token, Guid UserId, string Username)> RegisterAsync()
    {
        var userId = Guid.NewGuid();
        var username = $"u{Guid.NewGuid():N}"[..16];
        var response = await new Proto.UserService.UserServiceClient(Channel).RegisterAsync(new Proto.RegisterRequest
        {
            UserId = userId.ToString("D"), Username = username, Password = "correct horse battery"
        });
        return (response.Session.Token, userId, username);
    }

    public async ValueTask DisposeAsync()
    {
        Channel.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class LocalPortFromHeader : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (int.TryParse(context.Request.Headers[PortHeader], out var port))
                    context.Connection.LocalPort = port;
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    public sealed class Browser
    {
        private readonly HttpClient _http;
        private string? _cookie;

        public Browser(HttpClient http, int? port)
        {
            _http = http;
            if (port is { } p)
                _http.DefaultRequestHeaders.Add(PortHeader, p.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        public string? Origin { get; set; } = "http://localhost";

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
                request.Content = JsonContent.Create(body);
            if (Origin is not null)
                request.Headers.Add("Origin", Origin);
            if (_cookie is not null)
                request.Headers.Add("Cookie", _cookie);

            var response = await _http.SendAsync(request);

            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
                _cookie = cookies.Select(cookie => cookie.Split(';')[0]).LastOrDefault(cookie => !cookie.EndsWith('='));

            return response;
        }

        public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path);

        public Task<HttpResponseMessage> PostAsync(string path, object? body = null) => SendAsync(HttpMethod.Post, path, body ?? new { });

        public async Task<JsonElement> JsonAsync(string path)
        {
            var response = await GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
    }
}

public class DashboardHttpTests
{
    private static async Task<string> CodeOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

    private static async Task<DashboardServer.Browser> SignedInAsync(DashboardServer server)
    {
        var browser = server.OnDashboardPort();
        var setup = await browser.PostAsync("/api/setup",
            new { code = server.SetupCode, username = "owner", password = "correct horse battery" });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        return browser;
    }

    [Fact]
    public async Task The_dashboard_answers_only_on_its_own_port_and_grpc_does_not_answer_there()
    {
        await using var server = await DashboardServer.StartAsync();

        var page = await server.OnDashboardPort().GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("default-src 'none'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("DENY", page.Headers.GetValues("X-Frame-Options").Single());

        Assert.Equal(HttpStatusCode.NotFound, (await server.OnClientPort().GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.OnClientPort().GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await server.OnDashboardPort().PostAsync("/raindrop.v1.ServerService/GetServerInfo")).StatusCode);

        // gRPC is still served where clients reach it.
        var info = await new Proto.ServerService.ServerServiceClient(server.Channel).GetServerInfoAsync(new Proto.GetServerInfoRequest());
        Assert.Equal(ServerIdentity.CurrentProtocolVersion, info.ProtocolVersion);
    }

    [Fact]
    public async Task Nothing_but_the_state_answers_until_the_owner_signs_in()
    {
        await using var server = await DashboardServer.StartAsync();
        var browser = server.OnDashboardPort();

        var state = await browser.JsonAsync("/api/state");
        Assert.False(state.GetProperty("setUp").GetBoolean());
        Assert.False(state.GetProperty("signedIn").GetBoolean());

        foreach (var path in new[] { "/api/status", "/api/history", "/api/recoveries", "/api/accounts/lookup?q=x" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync(path)).StatusCode);

        // A page elsewhere cannot set it up through somebody's browser, even with the code.
        browser.Origin = "https://evil.example";
        var forged = await browser.PostAsync("/api/setup", new { code = server.SetupCode, username = "x", password = "correct horse battery" });
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        Assert.Equal("cross_origin", await CodeOf(forged));

        browser.Origin = "http://localhost";
        var wrong = await browser.PostAsync("/api/setup", new { code = "AAAA-AAAA-AAAA", username = "x", password = "correct horse battery" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("invalid_setup_code", await CodeOf(wrong));
    }

    [Fact]
    public async Task Signed_in_the_owner_sees_the_accounts_and_sessions_the_server_has()
    {
        await using var server = await DashboardServer.StartAsync();
        await server.RegisterAsync();
        await server.RegisterAsync();

        var browser = await SignedInAsync(server);
        var status = await browser.JsonAsync("/api/status");

        Assert.Equal(2, status.GetProperty("accounts").GetProperty("active").GetInt64());
        Assert.Equal(2, status.GetProperty("clients").GetProperty("sessions").GetInt32());
        Assert.Equal(0, status.GetProperty("clients").GetProperty("eventStreams").GetInt32());
        Assert.Equal("sqlite", status.GetProperty("server").GetProperty("database").GetString());
        Assert.Contains(status.GetProperty("environment").GetProperty("kind").GetString(), new[] { "host", "container" });

        server.Services.GetRequiredService<DashboardSampler>().Sample();
        var history = await browser.JsonAsync("/api/history");
        Assert.NotEmpty(history.GetProperty("samples").EnumerateArray());

        // Signing out ends it.
        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("/api/logout")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/status")).StatusCode);
    }

    [Fact]
    public async Task The_owner_issues_lists_and_cancels_a_recovery_code_that_the_server_then_accepts()
    {
        await using var server = await DashboardServer.StartAsync();
        var member = await server.RegisterAsync();
        var browser = await SignedInAsync(server);
        var claims = server.Services.GetRequiredService<AccountClaimService>();

        // Found by username (with an @) and by account id.
        var byName = await browser.JsonAsync($"/api/accounts/lookup?q=@{member.Username.ToUpperInvariant()}");
        Assert.Equal(member.UserId, byName.GetProperty("userId").GetGuid());
        Assert.Equal("active", byName.GetProperty("state").GetString());
        var byId = await browser.JsonAsync($"/api/accounts/lookup?q={member.UserId}");
        Assert.Equal(member.Username, byId.GetProperty("username").GetString());

        var unknown = await browser.GetAsync("/api/accounts/lookup?q=nobody-here");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("user_not_found", await CodeOf(unknown));

        // Issued: the code the response carries is one the sign-in path accepts.
        var issued = await browser.PostAsync($"/api/accounts/{member.UserId}/recovery");
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        var code = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
        Assert.StartsWith($"RDC1-{member.UserId:D}-", code);

        var pending = Assert.Single((await browser.JsonAsync("/api/recoveries")).EnumerateArray());
        Assert.Equal(member.UserId, pending.GetProperty("userId").GetGuid());
        Assert.False(pending.TryGetProperty("code", out _));

        // Cancelled: it no longer works.
        Assert.Equal(HttpStatusCode.NoContent,
            (await browser.SendAsync(HttpMethod.Delete, $"/api/recoveries/{member.UserId}")).StatusCode);
        Assert.Empty((await browser.JsonAsync("/api/recoveries")).EnumerateArray());
        Assert.False(claims.TryRedeem(member.UserId, code));
        Assert.Equal(HttpStatusCode.NotFound,
            (await browser.SendAsync(HttpMethod.Delete, $"/api/recoveries/{member.UserId}")).StatusCode);

        // Issued again, it works once.
        var again = await browser.PostAsync($"/api/accounts/{member.UserId}/recovery");
        var secondCode = (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
        Assert.True(claims.TryRedeem(member.UserId, secondCode));

        // None of it without a session.
        var stranger = server.OnDashboardPort();
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.PostAsync($"/api/accounts/{member.UserId}/recovery")).StatusCode);
    }
}
