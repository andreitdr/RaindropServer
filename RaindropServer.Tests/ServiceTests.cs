using System.Net;
using System.Net.Sockets;
using System.Text;
using RaindropServer.Channels;
using RaindropServer.Channels.Voice;
using RaindropServer.E2ee;
using RaindropServer.Roles;
using RaindropServer.Users;
using RaindropServer.Voice.LiveKit;

namespace RaindropServer.Tests;

/// <summary>A membership policy a test can change mid-way.</summary>
public sealed class SwitchablePolicy : IE2eeMembershipPolicy
{
    public HashSet<Guid> Allowed { get; } = [];

    public Task<bool> IsAllowedAsync(Guid userId, ChannelRecord channel, CancellationToken cancellationToken) =>
        Task.FromResult(Allowed.Contains(userId));
}

public class DeliveryServiceTests
{
    private sealed record Setup(
        MlsDeliveryService Delivery,
        SwitchablePolicy Policy,
        RecordingPublisher Events,
        IDeviceStore Devices,
        IUserStore Users,
        ChannelRecord Channel);

    private static async Task<Setup> CreateAsync()
    {
        var (driver, options) = await TestDatabases.OpenAsync("sqlite");
        var channels = driver.CreateChannelStore(options);
        var users = driver.CreateUserStore(options);
        var devices = driver.CreateDeviceStore(options);
        var policy = new SwitchablePolicy();
        var events = new RecordingPublisher();
        var delivery = new MlsDeliveryService(
            driver.CreateMlsGroupStore(options),
            driver.CreateKeyPackageStore(options),
            driver.CreateWelcomeStore(options),
            devices, channels, users, policy, events, new E2eeOptions());
        var channel = await Sessions.ChannelAsync(channels, Fixtures.ChannelId);
        return new Setup(delivery, policy, events, devices, users, channel);
    }

    /// <summary>An active user with an active device, allowed in the channel, and a session bound to it.</summary>
    private static async Task<UserSession> MemberAsync(Setup setup, bool allowed = true)
    {
        var user = await Sessions.ActiveUserAsync(setup.Users);
        var deviceId = Guid.NewGuid();
        await setup.Devices.AddDeviceAsync(
            new DeviceRecord(user.UserId, deviceId, new byte[32], [1], DateTime.UtcNow, null), default);
        if (allowed) setup.Policy.Allowed.Add(user.UserId);
        return Sessions.For(user.UserId, deviceId);
    }

    private static async Task<string> CodeOf(Func<Task> call) =>
        (await Assert.ThrowsAsync<ApiException>(call)).Code;

    [Fact]
    public async Task Only_a_bound_session_of_somebody_allowed_in_takes_part()
    {
        var setup = await CreateAsync();
        var member = await MemberAsync(setup);
        var outsider = await MemberAsync(setup, allowed: false);
        var request = new GroupRequest(setup.Channel.ChannelId);

        Assert.Equal("device_required",
            await CodeOf(() => setup.Delivery.GetStateAsync(member with { DeviceId = null }, request, default)));
        Assert.Equal("channel_not_found",
            await CodeOf(() => setup.Delivery.GetStateAsync(outsider, request, default)));
        Assert.Equal("group_not_found",
            await CodeOf(() => setup.Delivery.GetStateAsync(member, request, default)));
    }

    [Fact]
    public async Task Commits_are_ordered_by_epoch_and_losing_access_cuts_the_traffic_at_once()
    {
        var setup = await CreateAsync();
        var alice = await MemberAsync(setup);
        var channelId = setup.Channel.ChannelId;

        // The group info fixture is for epoch 1.
        var state = await setup.Delivery.CreateGroupAsync(alice, new CreateGroupRequest(channelId, Fixtures.Load("group_info")), default);
        Assert.Equal(1, state.Epoch);
        Assert.Equal("group_exists", await CodeOf(() =>
            setup.Delivery.CreateGroupAsync(alice, new CreateGroupRequest(channelId, Fixtures.Load("group_info")), default)));

        // A commit made in epoch 0 is too old; the external commit (epoch 1) is current.
        Assert.Equal("epoch_conflict", await CodeOf(() => setup.Delivery.SubmitCommitAsync(
            alice, new SubmitCommitRequest(channelId, Fixtures.Load("private_commit"), null, null), default)));
        var accepted = await setup.Delivery.SubmitCommitAsync(
            alice, new SubmitCommitRequest(channelId, Fixtures.Load("external_commit"), null, null), default);
        Assert.Equal(2, accepted.Epoch);
        Assert.Equal("epoch_conflict", await CodeOf(() => setup.Delivery.SubmitCommitAsync(
            alice, new SubmitCommitRequest(channelId, Fixtures.Load("external_commit"), null, null), default)));

        // Not a commit at all, or for another group: refused before it touches the log.
        Assert.Equal("invalid_mls_message", await CodeOf(() => setup.Delivery.SubmitCommitAsync(
            alice, new SubmitCommitRequest(channelId, Fixtures.Load("application"), null, null), default)));

        // The accepted commit went to the group's audience; Alice is in it.
        var (type, _, audience) = setup.Events.Published.Single(e => e.Type == DeliveryEventTypes.Handshake);
        Assert.True(await audience!(alice.UserId, default));

        // Alice loses access: the very next request and fan-out leave her out.
        setup.Policy.Allowed.Remove(alice.UserId);
        Assert.False(await audience(alice.UserId, default));
        Assert.Equal("channel_not_found", await CodeOf(() =>
            setup.Delivery.SyncAsync(alice, new SyncGroupRequest(channelId, 0), default)));
    }

    [Fact]
    public async Task A_welcome_goes_only_to_devices_that_belong_and_is_withheld_after_access_is_lost()
    {
        var setup = await CreateAsync();
        var alice = await MemberAsync(setup);
        var bob = await MemberAsync(setup);
        var outsider = await MemberAsync(setup, allowed: false);
        var channelId = setup.Channel.ChannelId;
        await setup.Delivery.CreateGroupAsync(alice, new CreateGroupRequest(channelId, Fixtures.Load("group_info")), default);

        DeviceRef Ref(UserSession s) => new(s.UserId, s.DeviceId!.Value);

        Assert.Equal("not_allowed_in_group", await CodeOf(() => setup.Delivery.SubmitCommitAsync(alice,
            new SubmitCommitRequest(channelId, Fixtures.Load("external_commit"), Fixtures.Load("welcome"), [Ref(outsider)]), default)));
        Assert.Equal("invalid_welcome", await CodeOf(() => setup.Delivery.SubmitCommitAsync(alice,
            new SubmitCommitRequest(channelId, Fixtures.Load("external_commit"), Fixtures.Load("welcome"), []), default)));
        Assert.Equal("invalid_mls_message", await CodeOf(() => setup.Delivery.SubmitCommitAsync(alice,
            new SubmitCommitRequest(channelId, Fixtures.Load("external_commit"), Fixtures.Load("key_package"), [Ref(bob)]), default)));

        await setup.Delivery.SubmitCommitAsync(alice,
            new SubmitCommitRequest(channelId, Fixtures.Load("external_commit"), Fixtures.Load("welcome"), [Ref(bob)]), default);
        Assert.Single((await setup.Delivery.FetchWelcomesAsync(bob, default)).Welcomes);

        setup.Policy.Allowed.Remove(bob.UserId);
        Assert.Empty((await setup.Delivery.FetchWelcomesAsync(bob, default)).Welcomes);
    }

    [Fact]
    public async Task Key_packages_must_be_key_packages_and_are_claimed_only_for_the_roster()
    {
        var setup = await CreateAsync();
        var alice = await MemberAsync(setup);
        var bob = await MemberAsync(setup);
        var outsider = await MemberAsync(setup, allowed: false);

        Assert.Equal("invalid_mls_message", await CodeOf(() => setup.Delivery.UploadKeyPackagesAsync(
            bob, new UploadKeyPackagesRequest([Fixtures.Load("welcome")], null), default)));
        var count = await setup.Delivery.UploadKeyPackagesAsync(
            bob, new UploadKeyPackagesRequest([Fixtures.Load("key_package"), Fixtures.Load("key_package")], null), default);
        Assert.Equal(2, count.OneTime);

        var roster = await setup.Delivery.GetRosterAsync(alice, new GroupRequest(setup.Channel.ChannelId), default);
        Assert.Contains(new DeviceRef(bob.UserId, bob.DeviceId!.Value), roster.Devices);
        Assert.DoesNotContain(roster.Devices, d => d.UserId == outsider.UserId);

        var claimed = await setup.Delivery.ClaimKeyPackagesAsync(alice, new ClaimKeyPackagesRequest(
            setup.Channel.ChannelId, [new DeviceRef(bob.UserId, bob.DeviceId!.Value)]), default);
        Assert.Single(claimed.KeyPackages);

        // A device that does not belong gets nothing — skipped, so the rest of a claim still works.
        var mixed = await setup.Delivery.ClaimKeyPackagesAsync(alice, new ClaimKeyPackagesRequest(
            setup.Channel.ChannelId,
            [new DeviceRef(outsider.UserId, outsider.DeviceId!.Value), new DeviceRef(bob.UserId, bob.DeviceId!.Value)]), default);
        Assert.Equal([bob.UserId], mixed.KeyPackages.Select(k => k.UserId));

        // A revoked device's session is refused even if it survived somehow.
        await setup.Devices.RevokeDeviceAsync(bob.UserId, bob.DeviceId!.Value, DateTime.UtcNow, default);
        Assert.Equal("invalid_session", await CodeOf(() => setup.Delivery.CountKeyPackagesAsync(bob, default)));
    }
}

public class DeviceServiceTests
{
    private static async Task<(DeviceService Service, IUserStore Users, InMemorySessionStore Sessions)> CreateAsync()
    {
        var (driver, options) = await TestDatabases.OpenAsync("sqlite");
        var users = driver.CreateUserStore(options);
        var sessions = new InMemorySessionStore(new UserOptions());
        var e2ee = new E2eeOptions();
        var service = new DeviceService(
            driver.CreateDeviceStore(options), driver.CreateKeyBackupStore(options), users,
            new Pbkdf2PasswordHasher(), sessions, new InMemoryDeviceLinkRelay(e2ee),
            new RecordingPublisher(), e2ee);
        return (service, users, sessions);
    }

    [Fact]
    public async Task A_certificate_registers_only_for_its_own_user_and_sets_the_identity_key()
    {
        var (service, users, _) = await CreateAsync();
        await Sessions.ActiveUserAsync(users, Fixtures.CertificateUserId);
        var owner = Sessions.For(Fixtures.CertificateUserId);
        var somebodyElse = Sessions.For((await Sessions.ActiveUserAsync(users)).UserId);
        var certificate = Fixtures.Load("certificate");

        var other = await Assert.ThrowsAsync<ApiException>(() =>
            service.RegisterDeviceAsync(somebodyElse, new RegisterDeviceRequest(certificate), default));
        Assert.Equal("certificate_mismatch", other.Code);

        var tampered = (byte[])certificate.Clone();
        tampered[^1] ^= 1;
        Assert.Equal("invalid_certificate", (await Assert.ThrowsAsync<ApiException>(() =>
            service.RegisterDeviceAsync(owner, new RegisterDeviceRequest(tampered), default))).Code);

        var registered = await service.RegisterDeviceAsync(owner, new RegisterDeviceRequest(certificate), default);
        Assert.Equal(Fixtures.CertificateDeviceId, registered.DeviceId);
        await service.RegisterDeviceAsync(owner, new RegisterDeviceRequest(certificate), default); // idempotent

        var listed = await service.ListDevicesAsync(somebodyElse,
            new ListDevicesRequest([Fixtures.CertificateUserId]), default);
        Assert.Equal(DeviceCertificate.Parse(certificate).UserIdentityKey, listed.Users.Single().IdentityKey);
    }

    [Fact]
    public async Task A_password_alone_cannot_change_what_encryption_depends_on()
    {
        var (service, users, _) = await CreateAsync();
        await Sessions.ActiveUserAsync(users, Fixtures.CertificateUserId);
        var passwordOnly = Sessions.For(Fixtures.CertificateUserId);
        await service.RegisterDeviceAsync(passwordOnly, new RegisterDeviceRequest(Fixtures.Load("certificate")), default);

        async Task<string> Code(Func<Task> call) => (await Assert.ThrowsAsync<ApiException>(call)).Code;

        Assert.Equal("device_required", await Code(() =>
            service.PutBackupAsync(passwordOnly, new PutKeyBackupRequest([1, 2, 3]), default)));
        Assert.Equal("device_required", await Code(() =>
            service.RevokeDeviceAsync(passwordOnly, new RevokeDeviceRequest(Fixtures.CertificateDeviceId), default)));
        Assert.Equal("device_required", await Code(() =>
            service.GetLinkOfferAsync(passwordOnly, new DeviceLinkRequest("ABCDEFGHJK"), default)));
    }

    [Fact]
    public async Task Binding_needs_a_fresh_challenge_and_the_device_key()
    {
        var (service, users, sessions) = await CreateAsync();
        var user = await Sessions.ActiveUserAsync(users, Fixtures.CertificateUserId);
        var session = sessions.Create(user.UserId, user.Username!);
        await service.RegisterDeviceAsync(session, new RegisterDeviceRequest(Fixtures.Load("certificate")), default);

        async Task<string> Code(Func<Task> call) => (await Assert.ThrowsAsync<ApiException>(call)).Code;

        Assert.Equal("no_challenge", await Code(() => service.BindSessionAsync(
            session, new BindSessionRequest(Fixtures.CertificateDeviceId, new byte[64]), default)));

        await service.CreateChallengeAsync(session, default);
        Assert.Equal("invalid_signature", await Code(() => service.BindSessionAsync(
            session, new BindSessionRequest(Fixtures.CertificateDeviceId, new byte[64]), default)));

        // Used up by the failed attempt: a second guess needs a new challenge.
        Assert.Equal("no_challenge", await Code(() => service.BindSessionAsync(
            session, new BindSessionRequest(Fixtures.CertificateDeviceId, new byte[64]), default)));
        Assert.True(sessions.TryValidate(session.Token, out var unbound));
        Assert.Null(unbound.DeviceId);
    }
}

/// <summary>
/// The room client against a stub LiveKit, for the answers that are not a plain success: what it
/// treats as "already gone" and what it reports.
/// </summary>
public class LiveKitRoomClientTests
{
    /// <summary>A LiveKit that gives every call the same canned answer, and remembers what it was asked.</summary>
    private sealed class StubLiveKit : IDisposable
    {
        private readonly HttpListener _listener = new();

        public StubLiveKit(HttpStatusCode status, string body)
        {
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}";
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

                    Calls.Add(context.Request.Url!.AbsolutePath);
                    context.Response.StatusCode = (int)status;
                    context.Response.ContentType = "application/json";
                    var bytes = Encoding.UTF8.GetBytes(body);
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            });
        }

        public string Url { get; }

        public List<string> Calls { get; } = [];

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
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

    private static LiveKitRoomClient Client(StubLiveKit livekit)
    {
        var options = new LiveKitOptions { ApiUrl = livekit.Url, ApiKey = "key", ApiSecret = new string('s', 48) };
        return new LiveKitRoomClient(options, new LiveKitJwt(options));
    }

    private static string Twirp(string code, string message) =>
        $$"""{"code":"{{code}}","msg":"{{message}}"}""";

    private static readonly Guid Channel = Guid.Parse("d592c66e-d5fe-47df-a103-5e77e77eefa9");
    private static readonly string Member = Guid.Parse("f3803c9b-723c-459b-a9d4-01653bf8d2b5").ToString("D");

    [Fact]
    public async Task A_participant_already_gone_is_not_an_error()
    {
        using var livekit = new StubLiveKit(HttpStatusCode.NotFound,
            Twirp("not_found", "twirp error unknown: participant does not exist"));
        using var client = Client(livekit);

        await client.RemoveParticipantAsync("raindrop-" + Channel, Member, default);

        Assert.Equal(["/twirp/livekit.RoomService/RemoveParticipant"], livekit.Calls);
    }

    /// <summary>
    /// Removing somebody at the moment they were leaving anyway: LiveKit waits for the node that held
    /// them, none answers, and it reports `unavailable`. They are gone, which is what was asked for.
    /// </summary>
    [Fact]
    public async Task A_participant_whose_node_no_longer_answers_is_not_an_error()
    {
        using var livekit = new StubLiveKit(HttpStatusCode.ServiceUnavailable,
            Twirp("unavailable", "twirp error unknown: no response from servers"));
        using var client = Client(livekit);

        await client.RemoveParticipantAsync("raindrop-" + Channel, Member, default);
        await client.UpdatePermissionAsync("raindrop-" + Channel, Member, canPublish: true, default);
        await client.DeleteRoomAsync("raindrop-" + Channel, default);
    }

    [Fact]
    public async Task Every_other_refusal_is_reported()
    {
        using var livekit = new StubLiveKit(HttpStatusCode.Unauthorized,
            Twirp("unauthenticated", "invalid API key"));
        using var client = Client(livekit);

        var error = await Assert.ThrowsAsync<VoiceTransportException>(() =>
            client.RemoveParticipantAsync("raindrop-" + Channel, Member, default));

        Assert.Contains("401", error.Message);
        Assert.Contains("invalid API key", error.Message);
    }

    /// <summary>
    /// A server-wide call has no room whose node could have gone: nothing answering means LiveKit
    /// itself is in trouble, and a reconciler told "no rooms" would tear down every live call.
    /// </summary>
    [Fact]
    public async Task A_server_wide_call_that_nothing_answers_is_reported()
    {
        using var livekit = new StubLiveKit(HttpStatusCode.ServiceUnavailable,
            Twirp("unavailable", "twirp error unknown: no response from servers"));
        using var client = Client(livekit);

        var error = await Assert.ThrowsAsync<VoiceTransportException>(() => client.ListRoomsAsync(default));

        Assert.Contains("ListRooms", error.Message);
    }
}

public class ReturningMemberTests
{
    private const string Password = "correct horse";

    private sealed record Setup(UserService Users, RoleService Roles);

    private static async Task<Setup> CreateAsync()
    {
        var (driver, options) = await TestDatabases.OpenAsync("sqlite");
        var users = driver.CreateUserStore(options);
        var roles = driver.CreateRoleStore(options);
        var roleOptions = new RoleOptions();
        await RoleService.EnsureBuiltInRolesAsync(roles, roleOptions, null, default);

        var events = new RecordingPublisher();
        var cache = new RolePermissionCache();
        var roleService = new RoleService(roles, users,
            new RolePermissionResolver(roles, users, roleOptions, cache), cache, roleOptions, events);
        var userOptions = new UserOptions();
        var userService = new UserService(users, new Pbkdf2PasswordHasher(1_000),
            new InMemorySessionStore(userOptions), userOptions, events,
            listeners: [new OwnerClaim(roleService)],
            leaveGuards: [new OwnerLeaveGuard(roleService)]);

        return new Setup(userService, roleService);
    }

    private static async Task<SessionResponse> RegisterAsync(Setup setup, string username) =>
        await setup.Users.RegisterAsync(new RegisterRequest(Guid.NewGuid(), username, Password), default);

    private static UserSession SessionOf(SessionResponse response) =>
        Sessions.For(response.User.UserId) with { Username = response.User.Username };

    [Fact]
    public async Task A_client_that_lost_its_uuid_logs_back_in_by_username_as_the_same_member()
    {
        var setup = await CreateAsync();
        var owner = await RegisterAsync(setup, "alice");

        var again = await setup.Users.LoginAsync(new LoginRequest(null, "Alice", Password), default);

        Assert.Equal(owner.User.UserId, again.User.UserId);
        Assert.True(await setup.Roles.IsSoleOwnerAsync(again.User.UserId, default));
    }

    [Fact]
    public async Task A_preserved_owner_rejoins_by_username_and_is_still_the_owner()
    {
        var setup = await CreateAsync();
        var owner = await RegisterAsync(setup, "alice");

        await setup.Users.LeaveAsync(SessionOf(owner), new LeaveRequest(Password, PreserveData: true), default);

        var left = await Assert.ThrowsAsync<ApiException>(() =>
            setup.Users.LoginAsync(new LoginRequest(null, "alice", Password), default));
        Assert.Equal("account_left", left.Code);

        var rejoined = await setup.Users.RejoinAsync(new RejoinRequest(null, "alice", Password), default);

        Assert.Equal(owner.User.UserId, rejoined.User.UserId);
        Assert.True(await setup.Roles.IsSoleOwnerAsync(rejoined.User.UserId, default));
    }

    [Fact]
    public async Task The_only_owner_cannot_erase_their_account_until_somebody_else_owns_the_server()
    {
        var setup = await CreateAsync();
        var owner = await RegisterAsync(setup, "alice");
        await RegisterAsync(setup, "bob");

        var refused = await Assert.ThrowsAsync<ApiException>(() =>
            setup.Users.LeaveAsync(SessionOf(owner), new LeaveRequest(Password, PreserveData: false), default));
        Assert.Equal("last_owner", refused.Code);

        // Refused before anything was written: the owner can still log in.
        await setup.Users.LoginAsync(new LoginRequest(owner.User.UserId, null, Password), default);

        await setup.Roles.GrantFromConsoleAsync("bob", "owner", default);
        var left = await setup.Users.LeaveAsync(SessionOf(owner), new LeaveRequest(Password, PreserveData: false), default);

        Assert.Equal(StateNames.Erased, left.State);
    }
}
