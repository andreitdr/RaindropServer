using System.Security.Cryptography;
using Google.Protobuf;
using Google.Rpc;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using RaindropServer.Channels;
using RaindropServer.E2ee;
using RaindropServer.Grpc;
using RaindropServer.Roles;
using RaindropServer.Users;
using Proto = RaindropServer.Grpc.Protocol;

namespace RaindropServer.Tests;

/// <summary>
/// Synthetic MLS messages: the cleartext framing the server reads, followed by bytes no member could
/// process — exactly what a member who wants to break a group can submit, since the server cannot read
/// past the framing.
/// </summary>
public static class Mls
{
    private static byte[] Frame(ushort wireFormat, byte[] header, byte[] groupId, long epoch, byte[] tail)
    {
        var bytes = new List<byte> { 0, 1, (byte)(wireFormat >> 8), (byte)wireFormat };
        bytes.AddRange(header);
        bytes.Add((byte)groupId.Length); // a one-byte vector length: group ids are under 64 bytes
        bytes.AddRange(groupId);
        var epochBytes = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(epochBytes, epoch);
        bytes.AddRange(epochBytes);
        bytes.AddRange(tail);
        return bytes.ToArray();
    }

    /// <summary>A private-message commit that decrypts for nobody.</summary>
    public static byte[] GarbageCommit(Guid channelId, long generation, long epoch, byte fill = 0xAB) =>
        Frame(2, [], MlsFraming.GroupIdFor(channelId, generation), epoch, [3, fill, fill, fill]);

    /// <summary>An application message's framing.</summary>
    public static byte[] Application(Guid channelId, long generation, long epoch) =>
        Frame(2, [], MlsFraming.GroupIdFor(channelId, generation), epoch, [1, 0xCD, 0xCD]);

    /// <summary>A group info's framing (version and cipher suite before the group id).</summary>
    public static byte[] GroupInfo(Guid channelId, long generation, long epoch, byte fill = 0xEF) =>
        Frame(4, [0, 1, 0, 1], MlsFraming.GroupIdFor(channelId, generation), epoch, [fill, fill]);
}

/// <summary>Grants <c>delete_channels</c> to the managers a test names, and only viewing to everybody else.</summary>
public sealed class ManagerPermissions : IPermissionResolver
{
    public HashSet<Guid> Managers { get; } = [];

    public Task<Permission> GetAsync(Guid userId, ChannelRecord? channel, CancellationToken cancellationToken) =>
        Task.FromResult(Managers.Contains(userId)
            ? Permission.ViewChannel | Permission.SendMessages | Permission.DeleteChannels
            : Permission.ViewChannel | Permission.SendMessages);
}

/// <summary>
/// Recovering a channel's group that a commit nobody can process (or a group info nobody can join
/// from) has broken: attribution, resets to a new generation, their limits, and bans.
/// </summary>
public class GroupRecoveryTests
{
    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record Setup(
        MlsDeliveryService Delivery,
        IMlsGroupStore Groups,
        SwitchablePolicy Policy,
        ManagerPermissions Permissions,
        RecordingPublisher Events,
        IDeviceStore Devices,
        IUserStore Users,
        ManualClock Clock,
        E2eeOptions Options,
        Guid ChannelId);

    private static async Task<Setup> CreateAsync()
    {
        var (driver, options) = await TestDatabases.OpenAsync("sqlite");
        var channels = driver.CreateChannelStore(options);
        var users = driver.CreateUserStore(options);
        var devices = driver.CreateDeviceStore(options);
        var groups = driver.CreateMlsGroupStore(options);
        var policy = new SwitchablePolicy();
        var permissions = new ManagerPermissions();
        var events = new RecordingPublisher();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var e2ee = new E2eeOptions();
        var delivery = new MlsDeliveryService(
            groups, driver.CreateKeyPackageStore(options), driver.CreateWelcomeStore(options),
            devices, channels, users, policy, permissions, events, e2ee, clock);
        var channel = await Sessions.ChannelAsync(channels, Guid.NewGuid());
        return new Setup(delivery, groups, policy, permissions, events, devices, users, clock, e2ee, channel.ChannelId);
    }

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

    private static DeviceRef Ref(UserSession session) => new(session.UserId, session.DeviceId!.Value);

    /// <summary>A group in generation 0 at epoch 0, created by <paramref name="creator"/>.</summary>
    private static Task<GroupStateResponse> CreateGroupAsync(Setup setup, UserSession creator, long generation = 0) =>
        setup.Delivery.CreateGroupAsync(creator,
            new CreateGroupRequest(setup.ChannelId, Mls.GroupInfo(setup.ChannelId, generation, 0)), default);

    private static Task<SubmitCommitResponse> SubmitAsync(Setup setup, UserSession session, byte[] commit) =>
        setup.Delivery.SubmitCommitAsync(session, new SubmitCommitRequest(setup.ChannelId, commit, null, null), default);

    private static Task<ResetGroupResponse> ReportAsync(
        Setup setup, UserSession session, long generation, long epoch, byte[]? reported) =>
        setup.Delivery.ResetGroupAsync(session,
            new ResetGroupRequest(setup.ChannelId, generation, epoch, reported is null ? null : SHA256.HashData(reported)),
            default);

    /// <summary>The same vector the client's <c>groupIdForChannel</c> test pins: both sides must build these bytes.</summary>
    [Fact]
    public void A_generation_after_0_appends_itself_to_the_channel_id()
    {
        var channel = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        Assert.Equal(MlsFraming.GroupIdFor(channel), MlsFraming.GroupIdFor(channel, 0));
        Assert.Equal("00112233445566778899aabbccddeeff0000000102030405",
            Convert.ToHexStringLower(MlsFraming.GroupIdFor(channel, 0x0102030405)));
        Assert.Throws<ArgumentOutOfRangeException>(() => MlsFraming.GroupIdFor(channel, -1));
    }

    [Fact]
    public async Task A_garbage_commit_is_attributed_and_reporting_it_moves_everyone_to_one_new_generation()
    {
        var setup = await CreateAsync();
        var alice = await MemberAsync(setup);
        var bob = await MemberAsync(setup);
        var mallory = await MemberAsync(setup);
        setup.Permissions.Managers.Add(alice.UserId);
        await CreateGroupAsync(setup, alice);

        // The server cannot tell: a commit for the current epoch is accepted, and recorded with the
        // device whose session submitted it.
        var garbage = Mls.GarbageCommit(setup.ChannelId, 0, 0);
        Assert.Equal(1, (await SubmitAsync(setup, mallory, garbage)).Epoch);
        var logged = Assert.Single(await setup.Groups.ReadLogAsync(setup.ChannelId, 0, 10, default));
        Assert.Equal(mallory.UserId, logged.SenderUserId);
        Assert.Equal(mallory.DeviceId, logged.SenderDeviceId);

        // A report must name what the server holds.
        Assert.Equal("report_mismatch", await CodeOf(() => ReportAsync(setup, bob, 0, 0, [1, 2, 3])));
        Assert.Equal("report_mismatch", await CodeOf(() => ReportAsync(setup, bob, 0, 1, garbage)));
        Assert.Equal("invalid_payload", await CodeOf(() => setup.Delivery.ResetGroupAsync(bob,
            new ResetGroupRequest(setup.ChannelId, 0, 0, [1, 2]), default)));
        Assert.Equal("invalid_payload", await CodeOf(() => ReportAsync(setup, bob, 5, 0, garbage)));

        var reset = await ReportAsync(setup, bob, 0, 0, garbage);
        Assert.Equal(new ResetGroupResponse(1, true), reset);
        var (_, data, audience) = setup.Events.Published.Single(e => e.Type == DeliveryEventTypes.GroupReset);
        Assert.Equal(new GroupResetEvent(setup.ChannelId, 1), data);
        Assert.True(await audience!(alice.UserId, default));

        // Alice found the same commit: her report changes nothing and tells her where everyone is.
        Assert.Equal(new ResetGroupResponse(1, false), await ReportAsync(setup, alice, 0, 0, garbage));
        Assert.Single(setup.Events.Published, e => e.Type == DeliveryEventTypes.GroupReset);

        // The old generation is gone: no group yet, a sync from it says rejoin, its commits are refused.
        var request = new GroupRequest(setup.ChannelId);
        Assert.Equal(1, (await setup.Delivery.GetGenerationAsync(alice, request, default)).Generation);
        Assert.Equal("group_not_found", await CodeOf(() => setup.Delivery.GetStateAsync(alice, request, default)));
        var sync = await setup.Delivery.SyncAsync(alice, new SyncGroupRequest(setup.ChannelId, 1, 0), default);
        Assert.True(sync.ResyncRequired);
        Assert.Equal(1, sync.Generation);
        Assert.Empty(await setup.Groups.ReadLogAsync(setup.ChannelId, 0, 10, default));

        // Only the device that reset may create the new generation at first, and only under its id.
        Assert.Equal("group_reserved", await CodeOf(() => CreateGroupAsync(setup, alice, 1)));
        Assert.Equal("wrong_group", await CodeOf(() => CreateGroupAsync(setup, bob, 0)));
        var created = await CreateGroupAsync(setup, bob, 1);
        Assert.Equal(1, created.Generation);
        Assert.Equal(0, created.Epoch);

        // Everyone else joins it; a commit for the old generation's id is refused.
        Assert.Equal("wrong_group", await CodeOf(() => SubmitAsync(setup, alice, Mls.GarbageCommit(setup.ChannelId, 0, 0))));
        Assert.Equal(1, (await SubmitAsync(setup, alice, Mls.GarbageCommit(setup.ChannelId, 1, 0, 0x01))).Epoch);
        var fresh = await setup.Delivery.SyncAsync(alice, new SyncGroupRequest(setup.ChannelId, 0, 1), default);
        Assert.False(fresh.ResyncRequired);
        Assert.Equal(1, fresh.Generation);
        Assert.Single(fresh.Commits);

        // A manager sees who reported what, and whose commit it was.
        Assert.Equal("missing_permission", await CodeOf(() => setup.Delivery.ListResetsAsync(bob, request, default)));
        var attributed = Assert.Single((await setup.Delivery.ListResetsAsync(alice, request, default)).Resets);
        Assert.Equal(1, attributed.Generation);
        Assert.Equal(bob.UserId, attributed.RequestedByUserId);
        Assert.Equal(bob.DeviceId, attributed.RequestedByDeviceId);
        Assert.Equal(GroupResetReports.Commit, attributed.ReportedKind);
        Assert.Equal(0, attributed.ReportedEpoch);
        Assert.Equal(mallory.UserId, attributed.SenderUserId);
        Assert.Equal(mallory.DeviceId, attributed.SenderDeviceId);
    }

    [Fact]
    public async Task The_reservation_lapses_so_a_reset_whose_device_never_creates_does_not_strand_the_channel()
    {
        var setup = await CreateAsync();
        var alice = await MemberAsync(setup);
        var bob = await MemberAsync(setup);
        await CreateGroupAsync(setup, alice);
        var garbage = Mls.GarbageCommit(setup.ChannelId, 0, 0);
        await SubmitAsync(setup, alice, garbage);
        await ReportAsync(setup, bob, 0, 0, garbage);

        Assert.Equal("group_reserved", await CodeOf(() => CreateGroupAsync(setup, alice, 1)));
        setup.Clock.Now += setup.Options.GroupCreatorReservation;
        Assert.Equal(1, (await CreateGroupAsync(setup, alice, 1)).Generation);
    }

    [Fact]
    public async Task Members_reset_at_most_once_per_cooldown_and_only_with_a_report_but_managers_may_always()
    {
        var setup = await CreateAsync();
        var bob = await MemberAsync(setup);
        var mallory = await MemberAsync(setup);
        var manager = await MemberAsync(setup);
        setup.Permissions.Managers.Add(manager.UserId);
        await CreateGroupAsync(setup, bob);

        var first = Mls.GarbageCommit(setup.ChannelId, 0, 0);
        await SubmitAsync(setup, mallory, first);
        Assert.Equal(1, (await ReportAsync(setup, bob, 0, 0, first)).Generation);
        await CreateGroupAsync(setup, bob, 1);

        // Mallory breaks the new generation at once; Bob has to wait to reset again.
        var second = Mls.GarbageCommit(setup.ChannelId, 1, 0);
        await SubmitAsync(setup, mallory, second);
        Assert.Equal("rate_limited", await CodeOf(() => ReportAsync(setup, bob, 1, 0, second)));
        setup.Clock.Now += setup.Options.GroupResetCooldown;
        Assert.Equal(new ResetGroupResponse(2, true), await ReportAsync(setup, bob, 1, 0, second));

        // Without a report only a manager may reset, and a manager is not limited.
        Assert.Equal("missing_permission", await CodeOf(() => ReportAsync(setup, mallory, 2, 0, null)));
        Assert.Equal(new ResetGroupResponse(3, true), await ReportAsync(setup, manager, 2, 0, null));
        Assert.Equal(new ResetGroupResponse(4, true), await ReportAsync(setup, manager, 3, 0, null));
        var latest = (await setup.Groups.ListResetsAsync(setup.ChannelId, 1, default)).Single();
        Assert.Null(latest.ReportedKind);
        Assert.Null(latest.SenderUserId);
    }

    [Fact]
    public async Task A_group_info_nobody_can_join_from_may_be_reported_once_it_has_had_time_to_catch_up()
    {
        var setup = await CreateAsync();
        var alice = await MemberAsync(setup);
        var bob = await MemberAsync(setup);
        await CreateGroupAsync(setup, alice);
        var info = Mls.GroupInfo(setup.ChannelId, 0, 0);

        // A commit moved the group on and nobody published a group info for epoch 1: a joiner can only
        // build on the one for epoch 0, which the server will refuse.
        await SubmitAsync(setup, alice, Mls.GarbageCommit(setup.ChannelId, 0, 0));
        Assert.Equal("group_info_pending", await CodeOf(() => ReportAsync(setup, bob, 0, 0, info)));

        setup.Clock.Now += setup.Options.GroupInfoGrace;
        Assert.Equal(new ResetGroupResponse(1, true), await ReportAsync(setup, bob, 0, 0, info));
        var reset = (await setup.Groups.ListResetsAsync(setup.ChannelId, 1, default)).Single();
        Assert.Equal(GroupResetReports.GroupInfo, reset.ReportedKind);
        Assert.Null(reset.SenderUserId);
    }

    [Fact]
    public async Task Somebody_outside_the_channel_cannot_reset_or_manage_it()
    {
        var setup = await CreateAsync();
        var alice = await MemberAsync(setup);
        var outsider = await MemberAsync(setup, allowed: false);
        setup.Permissions.Managers.Add(outsider.UserId); // a manager elsewhere, but not let into this channel
        await CreateGroupAsync(setup, alice);
        var garbage = Mls.GarbageCommit(setup.ChannelId, 0, 0);
        await SubmitAsync(setup, alice, garbage);

        Assert.Equal("channel_not_found", await CodeOf(() => ReportAsync(setup, outsider, 0, 0, garbage)));
        Assert.Equal("channel_not_found", await CodeOf(() => ReportAsync(setup, outsider, 0, 0, null)));
        Assert.Equal("channel_not_found", await CodeOf(() => setup.Delivery.BanAsync(outsider,
            new GroupBanRequest(setup.ChannelId, alice.UserId, null), default)));
        Assert.Equal("device_required", await CodeOf(() => ReportAsync(setup, alice with { DeviceId = null }, 0, 0, garbage)));
        Assert.Equal(0, await setup.Groups.GenerationAsync(setup.ChannelId, default));
    }

    [Fact]
    public async Task A_banned_device_or_account_is_kept_out_of_the_group_and_members_are_told_to_remove_it()
    {
        var setup = await CreateAsync();
        var alice = await MemberAsync(setup);
        var mallory = await MemberAsync(setup);
        setup.Permissions.Managers.Add(alice.UserId);
        await CreateGroupAsync(setup, alice);
        await setup.Delivery.UploadKeyPackagesAsync(mallory,
            new UploadKeyPackagesRequest([Fixtures.Load("key_package")], null), default);
        var request = new GroupRequest(setup.ChannelId);

        Assert.Equal("missing_permission", await CodeOf(() => setup.Delivery.BanAsync(mallory,
            new GroupBanRequest(setup.ChannelId, alice.UserId, null), default)));

        var device = new GroupBanRequest(setup.ChannelId, mallory.UserId, mallory.DeviceId);
        Assert.True((await setup.Delivery.BanAsync(alice, device, default)).Changed);
        Assert.False((await setup.Delivery.BanAsync(alice, device, default)).Changed);
        Assert.Contains(setup.Events.Published, e => e.Type == DeliveryEventTypes.GroupReconcile);

        // Off the roster, unclaimable, unwelcome, and refused on every call for the channel.
        Assert.DoesNotContain(Ref(mallory), (await setup.Delivery.GetRosterAsync(alice, request, default)).Devices);
        Assert.Empty((await setup.Delivery.ClaimKeyPackagesAsync(alice,
            new ClaimKeyPackagesRequest(setup.ChannelId, [Ref(mallory)]), default)).KeyPackages);
        Assert.Equal("not_allowed_in_group", await CodeOf(() => setup.Delivery.SubmitCommitAsync(alice,
            new SubmitCommitRequest(setup.ChannelId, Mls.GarbageCommit(setup.ChannelId, 0, 0), Fixtures.Load("welcome"),
                [Ref(mallory)]), default)));
        Assert.Equal("not_allowed_in_group", await CodeOf(() => SubmitAsync(setup, mallory, Mls.GarbageCommit(setup.ChannelId, 0, 0))));
        Assert.Equal("not_allowed_in_group", await CodeOf(() => setup.Delivery.SyncAsync(mallory,
            new SyncGroupRequest(setup.ChannelId, 0), default)));

        // A device ban leaves the account's other devices alone; an account ban takes them all, and
        // the group's traffic stops reaching the account.
        var malloryAgain = Sessions.For(mallory.UserId, Guid.NewGuid());
        await setup.Devices.AddDeviceAsync(
            new DeviceRecord(mallory.UserId, malloryAgain.DeviceId!.Value, new byte[32], [1], DateTime.UtcNow, null), default);
        Assert.Contains(Ref(malloryAgain), (await setup.Delivery.GetRosterAsync(alice, request, default)).Devices);
        await setup.Delivery.BanAsync(alice, new GroupBanRequest(setup.ChannelId, mallory.UserId, null), default);
        Assert.DoesNotContain((await setup.Delivery.GetRosterAsync(alice, request, default)).Devices, d => d.UserId == mallory.UserId);
        Assert.Equal("not_allowed_in_group", await CodeOf(() => setup.Delivery.GetStateAsync(malloryAgain, request, default)));
        await SubmitAsync(setup, alice, Mls.GarbageCommit(setup.ChannelId, 0, 0));
        var (_, _, audience) = setup.Events.Published.Last(e => e.Type == DeliveryEventTypes.Handshake);
        Assert.False(await audience!(mallory.UserId, default));
        Assert.True(await audience(alice.UserId, default));

        var bans = (await setup.Delivery.ListBansAsync(alice, request, default)).Bans;
        Assert.Equal(2, bans.Count);
        Assert.Contains(bans, b => b.DeviceId is null && b.BannedBy == alice.UserId);

        Assert.True((await setup.Delivery.UnbanAsync(alice, new GroupBanRequest(setup.ChannelId, mallory.UserId, null), default)).Changed);
        Assert.True((await setup.Delivery.UnbanAsync(alice, device, default)).Changed);
        Assert.False((await setup.Delivery.UnbanAsync(alice, device, default)).Changed);
        Assert.Equal(1, (await setup.Delivery.GetStateAsync(mallory, request, default)).Epoch);
    }
}

/// <summary>The same, over the wire: the new calls' shapes, and an encrypted message bound to its generation.</summary>
public class GroupRecoveryGrpcTests
{
    private static string ReasonOf(RpcException exception) =>
        exception.GetRpcStatus()?.GetDetail<ErrorInfo>() is { Domain: RaindropInterceptor.ErrorDomain } info
            ? info.Reason
            : throw new Xunit.Sdk.XunitException($"No Raindrop ErrorInfo on {exception.Status}.");

    /// <summary>A member with a registered device and a session bound to it.</summary>
    private static async Task<(string Token, Guid UserId, Guid DeviceId)> MemberAsync(GrpcServer server)
    {
        var (token, userId) = await server.RegisterAsync();
        var user = Guid.Parse(userId);
        var device = Guid.NewGuid();
        await server.Services.GetRequiredService<IDeviceStore>().AddDeviceAsync(
            new DeviceRecord(user, device, new byte[32], [1], DateTime.UtcNow, null), default);
        server.Services.GetRequiredService<ISessionStore>().BindDevice(token, device);
        return (token, user, device);
    }

    [Fact]
    public async Task A_broken_group_is_reported_reset_rejoined_and_attributed_over_the_wire()
    {
        await using var server = await GrpcServer.StartAsync();
        // The first to register owns the server, and so manages every channel.
        var owner = await MemberAsync(server);
        var member = await MemberAsync(server);
        var delivery = new Proto.DeliveryService.DeliveryServiceClient(server.Channel);
        var text = new Proto.TextChannelService.TextChannelServiceClient(server.Channel);
        var created = await new Proto.ChannelService.ChannelServiceClient(server.Channel).CreateChannelAsync(
            new Proto.CreateChannelRequest { Type = "text", Name = $"r{Guid.NewGuid():N}"[..16] }, GrpcServer.As(owner.Token));
        var channelId = created.Channel.ChannelId;
        var channel = Guid.Parse(channelId);

        await delivery.CreateGroupAsync(new Proto.CreateGroupRequest
        {
            ChannelId = channelId, GroupInfo = ByteString.CopyFrom(Mls.GroupInfo(channel, 0, 0))
        }, GrpcServer.As(owner.Token));
        var garbage = Mls.GarbageCommit(channel, 0, 0);
        await delivery.SubmitCommitAsync(new Proto.SubmitCommitRequest
        {
            ChannelId = channelId, Commit = ByteString.CopyFrom(garbage)
        }, GrpcServer.As(member.Token));

        var reset = await delivery.ResetGroupAsync(new Proto.ResetGroupRequest
        {
            ChannelId = channelId, Generation = 0, ReportedEpoch = 0,
            ReportedSha256 = ByteString.CopyFrom(SHA256.HashData(garbage))
        }, GrpcServer.As(owner.Token));
        Assert.Equal(1, reset.Generation);
        Assert.True(reset.Reset);

        Assert.Equal(1, (await delivery.GetGroupGenerationAsync(
            new Proto.GetGroupGenerationRequest { ChannelId = channelId }, GrpcServer.As(member.Token))).Generation);
        var stale = await delivery.SyncGroupAsync(
            new Proto.SyncGroupRequest { ChannelId = channelId, FromEpoch = 1, Generation = 0 }, GrpcServer.As(member.Token));
        Assert.True(stale.ResyncRequired);
        Assert.Equal(1, stale.Generation);

        var state = await delivery.CreateGroupAsync(new Proto.CreateGroupRequest
        {
            ChannelId = channelId, GroupInfo = ByteString.CopyFrom(Mls.GroupInfo(channel, 1, 0))
        }, GrpcServer.As(owner.Token));
        Assert.Equal(1, state.State.Generation);

        // A message encrypted in the old generation's group is refused; one in the new one is stored.
        var old = await Assert.ThrowsAsync<RpcException>(() => text.SendEncryptedMessageAsync(new Proto.SendEncryptedMessageRequest
        {
            ChannelId = channelId, Message = ByteString.CopyFrom(Mls.Application(channel, 0, 0))
        }, GrpcServer.As(member.Token)).ResponseAsync);
        Assert.Equal("wrong_group", ReasonOf(old));
        await text.SendEncryptedMessageAsync(new Proto.SendEncryptedMessageRequest
        {
            ChannelId = channelId, Message = ByteString.CopyFrom(Mls.Application(channel, 1, 0))
        }, GrpcServer.As(member.Token));

        var resets = await delivery.ListGroupResetsAsync(
            new Proto.ListGroupResetsRequest { ChannelId = channelId }, GrpcServer.As(owner.Token));
        var attributed = Assert.Single(resets.Resets);
        Assert.Equal("commit", attributed.ReportedKind);
        Assert.Equal(member.UserId.ToString(), attributed.ReportedSenderUserId);
        Assert.Equal(member.DeviceId.ToString(), attributed.ReportedSenderDeviceId);
        Assert.Equal(owner.DeviceId.ToString(), attributed.RequestedByDeviceId);

        var refused = await Assert.ThrowsAsync<RpcException>(() => delivery.ListGroupResetsAsync(
            new Proto.ListGroupResetsRequest { ChannelId = channelId }, GrpcServer.As(member.Token)).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, refused.StatusCode);
        Assert.Equal("missing_permission", ReasonOf(refused));

        // An empty device id bans every device of the account.
        Assert.True((await delivery.BanFromGroupAsync(new Proto.BanFromGroupRequest
        {
            ChannelId = channelId, UserId = member.UserId.ToString()
        }, GrpcServer.As(owner.Token))).Changed);
        var ban = Assert.Single((await delivery.ListGroupBansAsync(
            new Proto.ListGroupBansRequest { ChannelId = channelId }, GrpcServer.As(owner.Token))).Bans);
        Assert.Equal("", ban.DeviceId);
        var kept = await Assert.ThrowsAsync<RpcException>(() => text.SendEncryptedMessageAsync(new Proto.SendEncryptedMessageRequest
        {
            ChannelId = channelId, Message = ByteString.CopyFrom(Mls.Application(channel, 1, 0))
        }, GrpcServer.As(member.Token)).ResponseAsync);
        Assert.Equal("not_allowed_in_group", ReasonOf(kept));
        Assert.True((await delivery.UnbanFromGroupAsync(new Proto.UnbanFromGroupRequest
        {
            ChannelId = channelId, UserId = member.UserId.ToString()
        }, GrpcServer.As(owner.Token))).Changed);
    }
}
