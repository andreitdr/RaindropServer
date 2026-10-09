using Google.Protobuf;
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

public class SessionStoreTests
{
    /// <summary>A clock the test moves by hand.</summary>
    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>
    /// Presence no longer reads sessions, so nothing walks them as a side effect: a lapsed one is only
    /// forgotten by the sweep, or by its token being used again.
    /// </summary>
    [Fact]
    public void The_sweep_forgets_lapsed_sessions_and_keeps_live_ones()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var options = new UserOptions { SessionIdleTimeout = TimeSpan.FromMinutes(10) };
        var sessions = new InMemorySessionStore(options, clock);

        var lapsed = sessions.Create(Guid.NewGuid(), "gone");
        clock.Now += TimeSpan.FromMinutes(6);
        var live = sessions.Create(Guid.NewGuid(), "here");
        clock.Now += TimeSpan.FromMinutes(6);

        Assert.Equal(1, sessions.RemoveExpired());
        Assert.False(sessions.IsActive(lapsed.Token));
        Assert.True(sessions.IsActive(live.Token));
        Assert.Equal(0, sessions.RemoveExpired());
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
            : this(status, Encoding.UTF8.GetBytes(body), "application/json")
        {
        }

        public StubLiveKit(HttpStatusCode status, byte[] body, string contentType)
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
                    RequestTypes.Add(context.Request.ContentType ?? "");
                    using (var received = new MemoryStream())
                    {
                        await context.Request.InputStream.CopyToAsync(received);
                        Requests.Add(received.ToArray());
                    }

                    context.Response.StatusCode = (int)status;
                    context.Response.ContentType = contentType;
                    await context.Response.OutputStream.WriteAsync(body);
                    context.Response.Close();
                }
            });
        }

        public string Url { get; }

        public List<string> Calls { get; } = [];

        public List<string> RequestTypes { get; } = [];

        public List<byte[]> Requests { get; } = [];

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

    /// <summary>
    /// The room API is Twirp's binary form: the request goes out as protobuf with LiveKit's field
    /// numbers, and the answer is read back from protobuf — dropping anybody already disconnected.
    /// </summary>
    [Fact]
    public async Task Calls_are_protobuf_both_ways()
    {
        var answer = new Voice.LiveKit.Protocol.ListParticipantsResponse
        {
            Participants =
            {
                new Voice.LiveKit.Protocol.ParticipantInfo
                {
                    Identity = Member,
                    State = Voice.LiveKit.Protocol.ParticipantInfo.Types.State.Active,
                    Permission = new Voice.LiveKit.Protocol.ParticipantPermission
                    {
                        CanPublish = true,
                        CanPublishSources =
                        {
                            Voice.LiveKit.Protocol.TrackSource.Microphone,
                            Voice.LiveKit.Protocol.TrackSource.ScreenShare,
                            Voice.LiveKit.Protocol.TrackSource.ScreenShareAudio
                        }
                    }
                },
                new Voice.LiveKit.Protocol.ParticipantInfo
                {
                    Identity = Guid.NewGuid().ToString("D"),
                    State = Voice.LiveKit.Protocol.ParticipantInfo.Types.State.Disconnected
                }
            }
        };
        using var livekit = new StubLiveKit(HttpStatusCode.OK, answer.ToByteArray(), "application/protobuf");
        using var client = Client(livekit);

        var participants = await client.ListParticipantsAsync("raindrop-" + Channel, default);

        // The sources are read back, not discarded: the reconciler compares every one of them.
        Assert.Equal(
            [new LiveKitParticipant(Member, "ACTIVE", new VoicePublishGrant(true, false, true, true))],
            participants);
        Assert.Equal(["application/protobuf"], livekit.RequestTypes);
        Assert.Equal("raindrop-" + Channel,
            Voice.LiveKit.Protocol.ListParticipantsRequest.Parser.ParseFrom(livekit.Requests[0]).Room);
    }

    /// <summary>
    /// What a mid-call permission change actually puts on the wire. The sources matter as much as
    /// <c>can_publish</c>: Raindrop's cap on simultaneous cameras is only real because the camera is
    /// absent from this list until a member is allowed one.
    /// </summary>
    [Theory]
    [InlineData(true, false, false, false, true, new[] { "microphone" })]
    [InlineData(true, true, false, false, true, new[] { "microphone", "camera" })]
    [InlineData(false, true, false, false, true, new[] { "camera" })]
    [InlineData(false, false, false, false, false, new[] { "microphone" })]
    [InlineData(true, false, true, false, true, new[] { "microphone", "screen_share" })]
    [InlineData(true, false, true, true, true, new[] { "microphone", "screen_share", "screen_share_audio" })]
    [InlineData(false, false, true, false, true, new[] { "screen_share" })]
    // A window's sound with no picture to belong to is never granted on its own.
    [InlineData(true, false, false, true, true, new[] { "microphone" })]
    public async Task A_permission_update_names_exactly_the_sources_the_member_may_publish(
        bool microphone, bool camera, bool screen, bool screenAudio, bool canPublish, string[] expected)
    {
        using var livekit = new StubLiveKit(
            HttpStatusCode.OK, new Voice.LiveKit.Protocol.ParticipantInfo().ToByteArray(), "application/protobuf");
        using var client = Client(livekit);

        await client.UpdatePermissionAsync(
            "raindrop-" + Channel, Member, new VoicePublishGrant(microphone, camera, screen, screenAudio), default);

        var sent = Voice.LiveKit.Protocol.UpdateParticipantRequest.Parser.ParseFrom(livekit.Requests[0]);
        Assert.Equal(canPublish, sent.Permission.CanPublish);
        Assert.Equal(expected, sent.Permission.CanPublishSources.Select(source => source switch
        {
            Voice.LiveKit.Protocol.TrackSource.Microphone => "microphone",
            Voice.LiveKit.Protocol.TrackSource.Camera => "camera",
            Voice.LiveKit.Protocol.TrackSource.ScreenShare => "screen_share",
            Voice.LiveKit.Protocol.TrackSource.ScreenShareAudio => "screen_share_audio",
            _ => source.ToString()
        }));

        // The data channel stays shut whatever else changes: it is what keeps key material off
        // LiveKit entirely.
        Assert.False(sent.Permission.CanPublishData);
        Assert.True(sent.Permission.CanSubscribe);
        Assert.False(sent.Permission.CanUpdateMetadata);
    }

    /// <summary>
    /// LiveKit reads an empty <c>can_publish_sources</c> as "every source", so a member allowed
    /// nothing must still have the list name something — <c>can_publish: false</c> is what says no.
    /// </summary>
    [Fact]
    public void A_member_allowed_nothing_is_never_sent_an_empty_source_list()
    {
        Assert.NotEmpty(LiveKitJwt.PublishableSources(VoicePublishGrant.None));

        foreach (var speak in new[] { true, false })
        foreach (var camera in new[] { true, false })
        foreach (var screen in new[] { true, false })
        foreach (var screenAudio in new[] { true, false })
        {
            var sources = LiveKitJwt.PublishableSources(new VoicePublishGrant(speak, camera, screen, screenAudio));
            Assert.NotEmpty(sources);

            // And the shared sound never goes out without the picture it belongs to.
            if (sources.Contains("screen_share_audio"))
                Assert.Contains("screen_share", sources);
        }
    }

    /// <summary>
    /// Reading a permission back the way LiveKit holds it: an empty source list with
    /// <c>can_publish</c> means every source, and without <c>can_publish</c> nothing at all.
    /// </summary>
    [Fact]
    public void A_permission_read_back_honours_LiveKits_empty_list_rule()
    {
        Assert.Equal(VoicePublishGrant.None, LiveKitRoomClient.GrantOf(null));
        Assert.Equal(VoicePublishGrant.None, LiveKitRoomClient.GrantOf(
            new Voice.LiveKit.Protocol.ParticipantPermission
            {
                CanPublish = false,
                CanPublishSources = { Voice.LiveKit.Protocol.TrackSource.Microphone }
            }));
        Assert.Equal(new VoicePublishGrant(true, true, true, true), LiveKitRoomClient.GrantOf(
            new Voice.LiveKit.Protocol.ParticipantPermission { CanPublish = true }));
    }

    /// <summary>The join token carries the same rule as a mid-call update, from the same helper.</summary>
    [Fact]
    public void A_join_token_grants_the_camera_only_when_the_grant_does()
    {
        var options = new LiveKitOptions { ApiUrl = "http://localhost:7880", ApiKey = "key", ApiSecret = new string('s', 48) };
        var jwt = new LiveKitJwt(options);
        var member = Guid.Parse(Member);

        Assert.Equal(["microphone"], Sources(new VoicePublishGrant(true, false, false, false)));
        Assert.Equal(["microphone", "camera"], Sources(new VoicePublishGrant(true, true, false, false)));
        Assert.Equal(["microphone", "screen_share", "screen_share_audio"],
            Sources(new VoicePublishGrant(true, false, true, true)));

        IEnumerable<string> Sources(VoicePublishGrant grant)
        {
            var (token, _) = jwt.CreateParticipantToken(new VoiceGrant(Channel, member, "alice", grant));

            var payload = token.Split('.')[1];
            var json = Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')
                .Replace('-', '+').Replace('_', '/'));

            return System.Text.Json.JsonDocument.Parse(json)
                .RootElement.GetProperty("video").GetProperty("canPublishSources")
                .EnumerateArray().Select(entry => entry.GetString()!);
        }
    }

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
        await client.UpdatePermissionAsync(
            "raindrop-" + Channel, Member, new VoicePublishGrant(true, false, false, false), default);
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

/// <summary>
/// The voice state store's whole-decision operations. Every one of them has to settle a race, so the
/// tests run them in parallel rather than in sequence: a cap enforced by a read followed by a write
/// passes a sequential test and fails in production.
/// </summary>
public class VoiceStateStoreTests
{
    private static readonly Guid Channel = Guid.Parse("d592c66e-d5fe-47df-a103-5e77e77eefa9");

    private static VoiceParticipantRecord Participant(Guid userId, bool cameraSuppressed = false)
    {
        var now = DateTime.UtcNow;
        return new VoiceParticipantRecord
        {
            ChannelId = Channel,
            UserId = userId,
            SessionToken = userId.ToString("N"),
            CameraSuppressed = cameraSuppressed,
            JoinedAtUtc = now,
            LastSeenUtc = now
        };
    }

    private static async Task<InMemoryVoiceStateStore> CallOf(params Guid[] members)
    {
        var store = new InMemoryVoiceStateStore();
        foreach (var member in members)
            await store.JoinAsync(Participant(member), capacity: 25, default);

        return store;
    }

    [Fact]
    public async Task A_camera_goes_on_and_off()
    {
        var alice = Guid.NewGuid();
        var store = await CallOf(alice);

        var on = await store.SetCameraAsync(alice, true, 6, null, default);
        Assert.True(on.Participant!.CameraOn);
        Assert.True(on.Participant.CanPublishCamera);
        Assert.False(on.Full);
        Assert.False(on.NotAllowed);

        var off = await store.SetCameraAsync(alice, false, 6, null, default);
        Assert.False(off.Participant!.CameraOn);
        Assert.False(off.Participant.CanPublishCamera);
    }

    [Fact]
    public async Task A_member_who_may_not_use_their_camera_is_refused()
    {
        var alice = Guid.NewGuid();
        var store = new InMemoryVoiceStateStore();
        await store.JoinAsync(Participant(alice, cameraSuppressed: true), capacity: 25, default);

        var outcome = await store.SetCameraAsync(alice, true, 6, null, default);

        Assert.True(outcome.NotAllowed);
        Assert.False(outcome.Full);
        Assert.Null(outcome.Participant);
        Assert.False((await store.FindAsync(alice, default))!.CameraOn);
    }

    /// <summary>
    /// Turning off a camera is never refused, even by somebody who has since lost the permission —
    /// otherwise losing it would leave a camera nobody could switch off.
    /// </summary>
    [Fact]
    public async Task A_camera_can_always_be_turned_off()
    {
        var alice = Guid.NewGuid();
        var store = await CallOf(alice);
        await store.SetCameraAsync(alice, true, 6, null, default);
        await store.UpdateAsync(alice, record => record with { CameraSuppressed = true }, null, default);

        var outcome = await store.SetCameraAsync(alice, false, 0, null, default);

        Assert.False(outcome.NotAllowed);
        Assert.False(outcome.Full);
        Assert.False(outcome.Participant!.CameraOn);
    }

    /// <summary>
    /// A member whose camera is already on must not be told the call is full: they are counted in it
    /// themselves, so a re-assertion of the same state would refuse at exactly the cap.
    /// </summary>
    [Fact]
    public async Task Asking_for_the_state_it_already_has_is_not_refused()
    {
        var alice = Guid.NewGuid();
        var store = await CallOf(alice);
        await store.SetCameraAsync(alice, true, 1, null, default);

        var again = await store.SetCameraAsync(alice, true, 1, null, default);

        Assert.False(again.Full);
        Assert.True(again.Participant!.CameraOn);
    }

    [Fact]
    public async Task The_cap_counts_only_this_call()
    {
        var elsewhere = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        var store = new InMemoryVoiceStateStore();
        await store.JoinAsync(Participant(alice), capacity: 25, default);
        await store.JoinAsync(Participant(bob) with { ChannelId = elsewhere }, capacity: 25, default);

        await store.SetCameraAsync(bob, true, 1, null, default);
        var outcome = await store.SetCameraAsync(alice, true, 1, null, default);

        Assert.False(outcome.Full);
        Assert.True(outcome.Participant!.CameraOn);
    }

    [Fact]
    public async Task A_camera_is_not_turned_on_for_a_session_that_has_since_been_replaced()
    {
        var alice = Guid.NewGuid();
        var store = await CallOf(alice);

        var outcome = await store.SetCameraAsync(
            alice, true, 6, record => record.SessionToken == "another device", default);

        Assert.Null(outcome.Participant);
        Assert.False((await store.FindAsync(alice, default))!.CameraOn);
    }

    /// <summary>
    /// The race the operation exists for: more members than places, all asking at once. Exactly the
    /// cap may win, and every loser must be told so rather than left half-changed.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    public async Task Only_as_many_cameras_as_the_cap_allows_can_win_a_race(int cap)
    {
        var members = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToArray();
        var store = await CallOf(members);

        var outcomes = await Task.WhenAll(members.Select(member =>
            Task.Run(() => store.SetCameraAsync(member, true, cap, null, default).AsTask())));

        Assert.Equal(cap, outcomes.Count(outcome => outcome.Participant?.CameraOn == true));
        Assert.Equal(members.Length - cap, outcomes.Count(outcome => outcome.Full));
        Assert.DoesNotContain(outcomes, outcome => outcome.NotAllowed);

        // And the store agrees with what it told the callers.
        var records = await store.ListAsync(Channel, default);
        Assert.Equal(cap, records.Count(record => record.CameraOn));
    }

    /// <summary>A cap of zero is how a server switches camera video off: nobody wins.</summary>
    [Fact]
    public async Task A_cap_of_zero_lets_nobody_publish()
    {
        var alice = Guid.NewGuid();
        var store = await CallOf(alice);

        var outcome = await store.SetCameraAsync(alice, true, 0, null, default);

        Assert.True(outcome.Full);
        Assert.Null(outcome.Participant);
    }
}

/// <summary>
/// The screen-share half of the voice state store: the same race as the camera's, plus the rules a
/// share has that a camera does not — a share that is already running is not counted again when it
/// changes what it carries, and a window's sound is refused to somebody who may not speak.
/// </summary>
public class VoiceScreenShareStoreTests
{
    private static readonly Guid Channel = Guid.Parse("d592c66e-d5fe-47df-a103-5e77e77eefa9");

    private static VoiceParticipantRecord Participant(Guid userId)
    {
        var now = DateTime.UtcNow;
        return new VoiceParticipantRecord
        {
            ChannelId = Channel,
            UserId = userId,
            SessionToken = userId.ToString("N"),
            JoinedAtUtc = now,
            LastSeenUtc = now
        };
    }

    private static async Task<InMemoryVoiceStateStore> CallOf(params Guid[] members)
    {
        var store = new InMemoryVoiceStateStore();
        foreach (var member in members)
            await store.JoinAsync(Participant(member), capacity: 25, default);

        return store;
    }

    [Fact]
    public async Task A_window_with_sound_is_granted_its_picture_and_its_sound()
    {
        var alice = Guid.NewGuid();
        var store = await CallOf(alice);

        var outcome = await store.SetScreenShareAsync(alice, true, ScreenShareKinds.Window, true, 2, null, default);

        var record = outcome.Participant!;
        Assert.True(record.ScreenShareOn);
        Assert.Equal(new VoicePublishGrant(true, false, true, true), record.Publish);
    }

    /// <summary>
    /// The grant is the last word on sound, whatever the record says: a screen never carries any, and
    /// neither does somebody who may not speak.
    /// </summary>
    [Fact]
    public void The_grant_never_carries_sound_for_a_screen_or_for_somebody_who_may_not_speak()
    {
        var sharing = Participant(Guid.NewGuid()) with
        {
            ScreenShareOn = true, ScreenShareKind = ScreenShareKinds.Window, ScreenShareAudio = true
        };

        Assert.True(sharing.CanPublishScreenAudio);
        Assert.False((sharing with { ScreenShareKind = ScreenShareKinds.Screen }).CanPublishScreenAudio);
        Assert.False((sharing with { ServerMute = true }).CanPublishScreenAudio);
        Assert.False((sharing with { Suppressed = true }).CanPublishScreenAudio);
        Assert.False((sharing with { ScreenShareSuppressed = true }).CanPublishScreen);

        // Their own mute does not stop it: it is a separate track from their microphone.
        Assert.True((sharing with { SelfMute = true, SelfDeaf = true }).CanPublishScreenAudio);
    }

    [Fact]
    public async Task Asking_for_sound_while_muted_for_everyone_is_refused_and_changes_nothing()
    {
        var alice = Guid.NewGuid();
        var store = await CallOf(alice);
        await store.UpdateAsync(alice, record => record with { ServerMute = true }, null, default);

        var outcome = await store.SetScreenShareAsync(alice, true, ScreenShareKinds.Window, true, 2, null, default);

        Assert.True(outcome.Muted);
        Assert.Null(outcome.Participant);
        Assert.False((await store.FindAsync(alice, default))!.ScreenShareOn);

        // Without sound, the same share is fine.
        var silent = await store.SetScreenShareAsync(alice, true, ScreenShareKinds.Window, false, 2, null, default);
        Assert.True(silent.Participant!.ScreenShareOn);
    }

    [Fact]
    public async Task Stopping_clears_what_was_shared_and_is_never_refused()
    {
        var alice = Guid.NewGuid();
        var store = await CallOf(alice);
        await store.SetScreenShareAsync(alice, true, ScreenShareKinds.Window, true, 2, null, default);
        await store.UpdateAsync(alice, record => record with { ScreenShareSuppressed = true }, null, default);

        var outcome = await store.SetScreenShareAsync(alice, false, null, false, 0, null, default);

        var record = outcome.Participant!;
        Assert.False(outcome.NotAllowed);
        Assert.False(record.ScreenShareOn);
        Assert.Null(record.ScreenShareKind);
        Assert.False(record.ScreenShareAudio);
    }

    /// <summary>
    /// A running share that changes what it carries — sound switched off because it could not be
    /// captured — holds the place it already has, even in a call that is otherwise full.
    /// </summary>
    [Fact]
    public async Task A_running_share_can_change_what_it_carries_in_a_full_call()
    {
        var alice = Guid.NewGuid();
        var store = await CallOf(alice);
        await store.SetScreenShareAsync(alice, true, ScreenShareKinds.Window, true, 1, null, default);

        var changed = await store.SetScreenShareAsync(alice, true, ScreenShareKinds.Window, false, 1, null, default);

        Assert.False(changed.Full);
        Assert.False(changed.Participant!.ScreenShareAudio);
        Assert.True(changed.Participant.ScreenShareOn);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Only_as_many_shares_as_the_cap_allows_can_win_a_race(int cap)
    {
        var members = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToArray();
        var store = await CallOf(members);

        var outcomes = await Task.WhenAll(members.Select(member => Task.Run(() =>
            store.SetScreenShareAsync(member, true, ScreenShareKinds.Screen, false, cap, null, default).AsTask())));

        Assert.Equal(cap, outcomes.Count(outcome => outcome.Participant?.ScreenShareOn == true));
        Assert.Equal(members.Length - cap, outcomes.Count(outcome => outcome.Full));

        var records = await store.ListAsync(Channel, default);
        Assert.Equal(cap, records.Count(record => record.ScreenShareOn));
    }
}
