using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Google.Rpc;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using RaindropServer.Attachments;
using RaindropServer.Channels.Voice;
using RaindropServer.Events;
using RaindropServer.Grpc;
using RaindropServer.Grpc.Services;
using RaindropServer.Users;
using Proto = RaindropServer.Grpc.Protocol;

namespace RaindropServer.Tests;

/// <summary>
/// The whole server — the real <see cref="ServerHost.Build"/>, the gRPC module and a SQLite database —
/// in memory, called the way a client calls it. What these cover is what only exists on the wire: who
/// may call what, how a refusal arrives, and the three streams.
/// </summary>
public sealed class GrpcServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string _folder;

    private GrpcServer(WebApplication app, string folder, GrpcChannel channel)
    {
        _app = app;
        _folder = folder;
        Channel = channel;
    }

    public GrpcChannel Channel { get; }

    public IServiceProvider Services => _app.Services;

    public static async Task<GrpcServer> StartAsync(
        Action<EventOptions>? events = null,
        IVoiceTransport? voice = null,
        Action<VoiceOptions>? configureVoice = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"raindrop-grpc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);

        var services = new ServiceCollection();

        // Before AddRaindropServer, which only fills in UnavailableVoiceTransport when nothing else
        // claimed the seam — the same ordering AddRaindropLiveKit relies on.
        if (voice is not null)
            services.AddSingleton(voice);

        services.AddRaindropServer(
            configureVoice: configureVoice,
            configureEvents: events,
            configureAttachments: attachments => attachments.StoragePath = Path.Combine(folder, "attachments"));
        services.AddRaindropGrpc();
        services.AddRaindropDatabase(database =>
        {
            database.Provider = "sqlite";
            database.ConnectionString = $"Data Source={Path.Combine(folder, "raindrop.db")}";
        });
        await services.InitializeRaindropDatabaseAsync();

        var app = ServerHost.Build(services, new ServerHostOptions(), builder => builder.WebHost.UseTestServer());
        await app.StartAsync();

        var channel = GrpcChannel.ForAddress(app.GetTestServer().BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = app.GetTestServer().CreateHandler()
        });

        return new GrpcServer(app, folder, channel);
    }

    /// <summary>Registers a member and returns their session token.</summary>
    public async Task<(string Token, string UserId)> RegisterAsync(string? username = null)
    {
        var userId = Guid.NewGuid().ToString("D");
        var response = await new Proto.UserService.UserServiceClient(Channel).RegisterAsync(new Proto.RegisterRequest
        {
            UserId = userId,
            Username = username ?? $"u{Guid.NewGuid():N}"[..16],
            Password = "correct horse battery"
        });

        return (response.Session.Token, userId);
    }

    public static Metadata As(string token) => new() { { "authorization", $"Bearer {token}" } };

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
}

public class GrpcTests
{
    /// <summary>The error code a refusal carries, as clients read it: <c>ErrorInfo.reason</c>.</summary>
    private static string ReasonOf(RpcException exception) =>
        exception.GetRpcStatus()?.GetDetail<ErrorInfo>() is { Domain: RaindropInterceptor.ErrorDomain } info
            ? info.Reason
            : throw new Xunit.Sdk.XunitException($"No Raindrop ErrorInfo on {exception.Status}.");

    private static async Task<RpcException> Refused(Func<Task> call) => await Assert.ThrowsAsync<RpcException>(call);

    // ── Who may call what ────────────────────────────────────────────────────

    [Fact]
    public async Task The_handshake_needs_no_session()
    {
        await using var server = await GrpcServer.StartAsync();

        var info = await new Proto.ServerService.ServerServiceClient(server.Channel)
            .GetServerInfoAsync(new Proto.GetServerInfoRequest());

        Assert.Equal(ServerIdentity.CurrentProtocolVersion, info.ProtocolVersion);
    }

    [Fact]
    public async Task Everything_else_needs_a_valid_session()
    {
        await using var server = await GrpcServer.StartAsync();
        var users = new Proto.UserService.UserServiceClient(server.Channel);

        var missing = await Refused(async () => await users.GetMeAsync(new Proto.GetMeRequest()));
        Assert.Equal(StatusCode.Unauthenticated, missing.StatusCode);
        Assert.Equal("missing_token", ReasonOf(missing));

        var invalid = await Refused(async () => await users.GetMeAsync(new Proto.GetMeRequest(), GrpcServer.As("nonsense")));
        Assert.Equal("invalid_session", ReasonOf(invalid));

        var (token, userId) = await server.RegisterAsync();
        var me = await users.GetMeAsync(new Proto.GetMeRequest(), GrpcServer.As(token));
        Assert.Equal(userId, me.User.UserId);
        Assert.Equal(Proto.UserState.Active, me.User.State);
    }

    // ── How a refusal arrives ────────────────────────────────────────────────

    [Fact]
    public async Task A_domain_refusal_is_a_status_with_its_code_as_the_reason()
    {
        await using var server = await GrpcServer.StartAsync();
        await server.RegisterAsync("alice");

        var taken = await Refused(() => server.RegisterAsync("alice"));

        Assert.Equal(StatusCode.FailedPrecondition, taken.StatusCode);
        Assert.Equal("username_taken", ReasonOf(taken));
        Assert.False(string.IsNullOrWhiteSpace(taken.Status.Detail));
    }

    [Fact]
    public async Task A_malformed_id_is_invalid_payload_and_an_empty_one_reaches_the_domain()
    {
        await using var server = await GrpcServer.StartAsync();
        var (token, _) = await server.RegisterAsync();
        var channels = new Proto.ChannelService.ChannelServiceClient(server.Channel);

        var malformed = await Refused(async () => await channels.GetChannelAsync(
            new Proto.GetChannelRequest { ChannelId = "not-a-uuid" }, GrpcServer.As(token)));
        Assert.Equal(StatusCode.InvalidArgument, malformed.StatusCode);
        Assert.Equal("invalid_payload", ReasonOf(malformed));

        var empty = await Refused(async () => await channels.GetChannelAsync(
            new Proto.GetChannelRequest(), GrpcServer.As(token)));
        Assert.Equal("channel_not_found", ReasonOf(empty));
    }

    // ── The event stream ─────────────────────────────────────────────────────

    private static async Task<Proto.ServerEvent> NextAsync(
        AsyncDuplexStreamingCall<Proto.ConnectRequest, Proto.ConnectResponse> stream,
        Proto.ServerEvent.EventOneofCase? skipUntil = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        while (await stream.ResponseStream.MoveNext(timeout.Token))
        {
            var next = stream.ResponseStream.Current.Event;
            if (skipUntil is null || next.EventCase == skipUntil)
                return next;
        }

        throw new Xunit.Sdk.XunitException("The stream ended without the event.");
    }

    private static async Task<RpcException> EndingAsync(AsyncDuplexStreamingCall<Proto.ConnectRequest, Proto.ConnectResponse> stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        return await Assert.ThrowsAsync<RpcException>(async () =>
        {
            while (await stream.ResponseStream.MoveNext(timeout.Token))
            {
            }
        });
    }

    [Fact]
    public async Task Ready_comes_first_then_changes_are_pushed_and_resubscribe_sends_ready_again()
    {
        await using var server = await GrpcServer.StartAsync();
        var (token, userId) = await server.RegisterAsync();
        using var stream = new Proto.EventService.EventServiceClient(server.Channel).Connect(GrpcServer.As(token));

        var ready = await NextAsync(stream);
        Assert.Equal(Proto.ServerEvent.EventOneofCase.Ready, ready.EventCase);
        Assert.Equal(userId, ready.Ready.UserId);
        Assert.Equal(new EventOptions().HeartbeatInterval, ready.Ready.HeartbeatInterval.ToTimeSpan());

        await new Proto.ChannelService.ChannelServiceClient(server.Channel).CreateChannelAsync(
            new Proto.CreateChannelRequest { Type = "text", Name = "general" }, GrpcServer.As(token));

        var created = await NextAsync(stream, Proto.ServerEvent.EventOneofCase.ChannelCreated);
        Assert.Equal("general", created.ChannelCreated.Name);

        await stream.RequestStream.WriteAsync(new Proto.ConnectRequest { Resubscribe = new Proto.Resubscribe() });
        await NextAsync(stream, Proto.ServerEvent.EventOneofCase.Ready);

        await stream.RequestStream.CompleteAsync();
    }

    [Fact]
    public async Task A_member_is_online_exactly_while_their_stream_is_open()
    {
        await using var server = await GrpcServer.StartAsync();
        var (token, userId) = await server.RegisterAsync();
        var members = new Proto.MemberService.MemberServiceClient(server.Channel);

        async Task<Proto.Presence> PresenceAsync() =>
            (await members.ListMembersAsync(new Proto.ListMembersRequest(), GrpcServer.As(token)))
            .Members.Single(member => member.UserId == userId).Presence;

        Assert.Equal(Proto.Presence.Offline, await PresenceAsync());

        using (var stream = new Proto.EventService.EventServiceClient(server.Channel).Connect(GrpcServer.As(token)))
        {
            await NextAsync(stream);
            var online = await NextAsync(stream, Proto.ServerEvent.EventOneofCase.MemberPresence);
            Assert.Equal(Proto.Presence.Online, online.MemberPresence.Presence);
            Assert.Equal(Proto.Presence.Online, await PresenceAsync());

            await stream.RequestStream.CompleteAsync();
            await stream.ResponseHeadersAsync;
        }

        // The registry lets the connection go once the call has wound down.
        for (var tries = 0; tries < 50 && await PresenceAsync() != Proto.Presence.Offline; tries++)
            await Task.Delay(50);

        Assert.Equal(Proto.Presence.Offline, await PresenceAsync());
    }

    [Fact]
    public async Task Logging_out_ends_the_stream_as_session_ended()
    {
        await using var server = await GrpcServer.StartAsync();
        var (token, _) = await server.RegisterAsync();
        using var stream = new Proto.EventService.EventServiceClient(server.Channel).Connect(GrpcServer.As(token));
        await NextAsync(stream);

        await new Proto.UserService.UserServiceClient(server.Channel).LogoutAsync(new Proto.LogoutRequest(), GrpcServer.As(token));

        var ended = await EndingAsync(stream);
        Assert.Equal(StatusCode.Unauthenticated, ended.StatusCode);
        Assert.Equal("session_ended", ReasonOf(ended));
    }

    [Fact]
    public async Task A_stream_without_heartbeats_is_ended_as_lost_and_heartbeats_keep_it()
    {
        var timeout = TimeSpan.FromMilliseconds(400);
        await using var server = await GrpcServer.StartAsync(events => events.HeartbeatTimeout = timeout);
        var (token, _) = await server.RegisterAsync();
        var events = new Proto.EventService.EventServiceClient(server.Channel);

        using (var kept = events.Connect(GrpcServer.As(token)))
        {
            await NextAsync(kept);

            // Three timeouts' worth of heartbeats: had they not counted, the stream would have ended.
            for (var i = 0; i < 12; i++)
            {
                await kept.RequestStream.WriteAsync(new Proto.ConnectRequest { Heartbeat = new Proto.Heartbeat() });
                await Task.Delay(timeout / 4);
            }

            await kept.RequestStream.WriteAsync(new Proto.ConnectRequest { Resubscribe = new Proto.Resubscribe() });
            await NextAsync(kept, Proto.ServerEvent.EventOneofCase.Ready);
            await kept.RequestStream.CompleteAsync();
        }

        using var silent = events.Connect(GrpcServer.As(token));
        await NextAsync(silent);

        var lost = await EndingAsync(silent);
        Assert.Equal(StatusCode.Unavailable, lost.StatusCode);
        Assert.Equal("connection_lost", ReasonOf(lost));
    }

    [Fact]
    public async Task A_plugin_event_travels_as_a_plugin_event_when_its_payload_is_protobuf()
    {
        await using var server = await GrpcServer.StartAsync();
        var (token, _) = await server.RegisterAsync();
        using var stream = new Proto.EventService.EventServiceClient(server.Channel).Connect(GrpcServer.As(token));
        await NextAsync(stream);

        var publisher = server.Services.GetRequiredService<IEventPublisher>();
        await publisher.PublishAsync("polls.not_protobuf", new { Question = "dropped" });
        await publisher.PublishAsync("polls.vote", new StringValue { Value = "yes" });

        var plugin = await NextAsync(stream, Proto.ServerEvent.EventOneofCase.Plugin);
        Assert.Equal("polls.vote", plugin.Plugin.Type);
        Assert.Equal("yes", StringValue.Parser.ParseFrom(plugin.Plugin.Data).Value);
    }

    // ── Attachments ──────────────────────────────────────────────────────────

    /// <summary>A member with a text channel and a session bound to a device, as uploading needs.</summary>
    private static async Task<(string Token, string ChannelId)> UploaderAsync(GrpcServer server)
    {
        var (token, _) = await server.RegisterAsync();
        server.Services.GetRequiredService<ISessionStore>().BindDevice(token, Guid.NewGuid());

        var channel = await new Proto.ChannelService.ChannelServiceClient(server.Channel).CreateChannelAsync(
            new Proto.CreateChannelRequest { Type = "text", Name = $"files-{Guid.NewGuid():N}"[..20] }, GrpcServer.As(token));

        return (token, channel.Channel.ChannelId);
    }

    private static async Task<Proto.UploadStatus> UploadAsync(
        Proto.AttachmentService.AttachmentServiceClient attachments, string token, string attachmentId, long offset,
        ReadOnlyMemory<byte> data)
    {
        using var upload = attachments.Upload(GrpcServer.As(token));
        await upload.RequestStream.WriteAsync(new Proto.UploadRequest
        {
            Header = new Proto.UploadHeader { AttachmentId = attachmentId, Offset = offset }
        });

        for (var at = 0; at < data.Length; at += AttachmentGrpcService.FrameBytes)
        {
            var frame = data.Slice(at, Math.Min(AttachmentGrpcService.FrameBytes, data.Length - at));
            await upload.RequestStream.WriteAsync(new Proto.UploadRequest { Data = ByteString.CopyFrom(frame.Span) });
        }

        await upload.RequestStream.CompleteAsync();
        return (await upload).Status;
    }

    [Fact]
    public async Task An_upload_streams_in_frames_resumes_from_where_it_stood_and_downloads_from_an_offset()
    {
        await using var server = await GrpcServer.StartAsync();
        var (token, channelId) = await UploaderAsync(server);
        var attachments = new Proto.AttachmentService.AttachmentServiceClient(server.Channel);

        // Several frames' worth, so frames straddle and the upload takes more than one stream.
        var ciphertext = RandomNumberGenerator.GetBytes((int)AttachmentFormat.CiphertextLength(700 * 1024));
        var created = await attachments.CreateUploadAsync(new Proto.CreateUploadRequest
        {
            ChannelId = channelId,
            Extension = "bin",
            CiphertextSize = ciphertext.Length
        }, GrpcServer.As(token));
        var id = created.Status.AttachmentId;
        Assert.Equal(AttachmentGrpcService.FrameBytes, created.Status.FrameBytes);

        var half = ciphertext.Length / 2;
        var first = await UploadAsync(attachments, token, id, 0, ciphertext.AsMemory(0, half));
        Assert.Equal(half, first.ReceivedSize);

        var wrongPlace = await Refused(() => UploadAsync(attachments, token, id, 0, ciphertext.AsMemory(0, 10)));
        Assert.Equal("upload_offset_mismatch", ReasonOf(wrongPlace));

        var status = await attachments.GetUploadStatusAsync(
            new Proto.GetUploadStatusRequest { AttachmentId = id }, GrpcServer.As(token));
        var rest = await UploadAsync(attachments, token, id, status.Status.ReceivedSize, ciphertext.AsMemory(half));
        Assert.Equal(ciphertext.Length, rest.ReceivedSize);

        var complete = await attachments.CompleteUploadAsync(new Proto.CompleteUploadRequest
        {
            AttachmentId = id,
            CiphertextSha256 = ByteString.CopyFrom(SHA256.HashData(ciphertext))
        }, GrpcServer.As(token));
        Assert.Equal(Proto.UploadState.Uploaded, complete.Status.State);

        // The uploader may read an upload before it is sent; resuming reads from where a client stopped.
        var from = ciphertext.Length - 300_000;
        using var download = attachments.Download(
            new Proto.DownloadRequest { AttachmentId = id, Offset = from }, GrpcServer.As(token));

        Assert.True(await download.ResponseStream.MoveNext());
        Assert.Equal(ciphertext.Length, download.ResponseStream.Current.Header.CiphertextSize);
        Assert.Equal(from, download.ResponseStream.Current.Header.Offset);

        using var received = new MemoryStream();
        while (await download.ResponseStream.MoveNext())
            download.ResponseStream.Current.Data.WriteTo(received);

        Assert.Equal(ciphertext.AsSpan(from).ToArray(), received.ToArray());
    }

    [Fact]
    public async Task A_frame_larger_than_the_frame_size_is_refused()
    {
        await using var server = await GrpcServer.StartAsync();
        var (token, channelId) = await UploaderAsync(server);
        var attachments = new Proto.AttachmentService.AttachmentServiceClient(server.Channel);

        var created = await attachments.CreateUploadAsync(new Proto.CreateUploadRequest
        {
            ChannelId = channelId,
            Extension = "bin",
            CiphertextSize = AttachmentFormat.CiphertextLength(600 * 1024)
        }, GrpcServer.As(token));

        using var upload = attachments.Upload(GrpcServer.As(token));
        await upload.RequestStream.WriteAsync(new Proto.UploadRequest
        {
            Header = new Proto.UploadHeader { AttachmentId = created.Status.AttachmentId }
        });
        await upload.RequestStream.WriteAsync(new Proto.UploadRequest
        {
            Data = ByteString.CopyFrom(new byte[AttachmentGrpcService.FrameBytes + 1])
        });
        await upload.RequestStream.CompleteAsync();

        var refused = await Refused(async () => await upload);
        Assert.Equal(StatusCode.ResourceExhausted, refused.StatusCode);
        Assert.Equal("chunk_too_large", ReasonOf(refused));
    }

    [Fact]
    public async Task Downloads_refuse_every_id_the_caller_cannot_reach_alike()
    {
        await using var server = await GrpcServer.StartAsync();
        var (token, _) = await server.RegisterAsync();

        using var download = new Proto.AttachmentService.AttachmentServiceClient(server.Channel).Download(
            new Proto.DownloadRequest { AttachmentId = Guid.NewGuid().ToString("D") }, GrpcServer.As(token));

        var refused = await Refused(async () => await download.ResponseStream.MoveNext());
        Assert.Equal("attachment_not_found", ReasonOf(refused));
    }
}

/// <summary>
/// A voice transport that carries nothing and remembers everything it was told — the seam
/// <c>IVoiceTransport</c> exists for, standing in for a LiveKit sidecar. Not a mock: it is a real
/// implementation whose "media server" is a dictionary, which is what lets the voice control plane
/// be tested through the wire at all.
/// </summary>
public sealed class FakeVoiceTransport : IVoiceTransport
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(Guid Channel, Guid User), TransportParticipant> _participants = new();

    public string Name => "fake";

    public bool IsAvailable => true;

    /// <summary>Every grant minted, in order — what a client's token would have allowed.</summary>
    public List<VoiceGrant> Grants { get; } = [];

    /// <summary>Every mid-call permission change pushed, in order.</summary>
    public List<(Guid Channel, Guid User, VoicePublishGrant Grant)> Pushed { get; } = [];

    public Task<VoiceConnection> CreateConnectionAsync(VoiceGrant grant, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Grants.Add(grant);
            _participants[(grant.ChannelId, grant.UserId)] = new TransportParticipant(grant.UserId, grant.Publish);
        }

        return Task.FromResult(new VoiceConnection(Name, null, "/api/channels/voice/signal", "token", DateTime.UtcNow.AddMinutes(2)));
    }

    public Task SetPublishAsync(
        Guid channelId, Guid userId, VoicePublishGrant grant, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Pushed.Add((channelId, userId, grant));
            if (_participants.ContainsKey((channelId, userId)))
                _participants[(channelId, userId)] = new TransportParticipant(userId, grant);
        }

        return Task.CompletedTask;
    }

    public Task DisconnectAsync(Guid channelId, Guid userId, CancellationToken cancellationToken)
    {
        lock (_gate)
            _participants.Remove((channelId, userId));

        return Task.CompletedTask;
    }

    public Task CloseCallAsync(Guid channelId, CancellationToken cancellationToken)
    {
        lock (_gate)
            foreach (var key in _participants.Keys.Where(key => key.Channel == channelId).ToList())
                _participants.Remove(key);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TransportCall>> ListCallsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<TransportCall> calls = _participants
                .GroupBy(entry => entry.Key.Channel)
                .Select(call => new TransportCall(call.Key, call.Select(entry => entry.Value).ToList()))
                .ToList();

            return Task.FromResult(calls);
        }
    }

    /// <summary>What this "media server" currently allows a participant, or null when it has none.</summary>
    public TransportParticipant? Allows(Guid channelId, Guid userId)
    {
        lock (_gate)
            return _participants.GetValueOrDefault((channelId, userId));
    }

    /// <summary>
    /// Waits for the push the work queue makes after a camera, share or mute change. The queue is
    /// deliberately off the request path, so a test that asserts straight after the call races it.
    /// </summary>
    public Task<bool> WaitForPushAsync(Guid channelId, Guid userId, bool canPublishCamera) =>
        WaitForGrantAsync(channelId, userId, grant => grant.Camera == canPublishCamera);

    /// <summary>Waits until what the "media server" allows a participant satisfies <paramref name="wanted"/>.</summary>
    public async Task<bool> WaitForGrantAsync(Guid channelId, Guid userId, Func<VoicePublishGrant, bool> wanted)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (Allows(channelId, userId) is { } allowed && wanted(allowed.Publish))
                return true;

            await Task.Delay(20);
        }

        return false;
    }
}

/// <summary>
/// Camera video in a call, over the wire: the <c>video</c> permission, the cap on how many cameras
/// one call carries, and the publishing grant the media server is given. The voice control plane had
/// no wire-level tests before camera video gave it a decision that can be refused.
/// </summary>
public class VoiceCameraGrpcTests
{
    private static string ReasonOf(RpcException exception) =>
        exception.GetRpcStatus()?.GetDetail<ErrorInfo>() is { Domain: RaindropInterceptor.ErrorDomain } info
            ? info.Reason
            : throw new Xunit.Sdk.XunitException($"No Raindrop ErrorInfo on {exception.Status}.");

    /// <summary>A server with a voice transport, one voice channel, and the owner in the call.</summary>
    private static async Task<(GrpcServer Server, FakeVoiceTransport Transport, string Channel, string Token, string UserId)>
        InCallAsync(int cameraCap = 6)
    {
        var transport = new FakeVoiceTransport();
        var server = await GrpcServer.StartAsync(
            voice: transport,
            configureVoice: options => options.MaximumCameraPublishersPerChannel = cameraCap);

        var (token, userId) = await server.RegisterAsync();

        var channel = await new Proto.ChannelService.ChannelServiceClient(server.Channel).CreateChannelAsync(
            new Proto.CreateChannelRequest { Type = "voice", Name = $"call-{Guid.NewGuid():N}"[..20] },
            GrpcServer.As(token));

        await new Proto.VoiceService.VoiceServiceClient(server.Channel).JoinVoiceAsync(
            new Proto.JoinVoiceRequest { ChannelId = channel.Channel.ChannelId }, GrpcServer.As(token));

        return (server, transport, channel.Channel.ChannelId, token, userId);
    }

    [Fact]
    public async Task A_call_reports_how_many_cameras_it_carries()
    {
        var (server, _, _, token, _) = await InCallAsync(cameraCap: 4);
        await using var _server = server;

        var status = await new Proto.VoiceService.VoiceServiceClient(server.Channel)
            .GetVoiceStatusAsync(new Proto.GetVoiceStatusRequest(), GrpcServer.As(token));

        Assert.Equal(4, status.MaximumCameraPublishers);
    }

    /// <summary>
    /// Joining never starts the camera, so the token a client gets is for the microphone alone. The
    /// camera is added only once they ask, which is what makes the cap real rather than advisory.
    /// </summary>
    [Fact]
    public async Task Joining_grants_the_microphone_and_not_the_camera()
    {
        var (server, transport, _, _, userId) = await InCallAsync();
        await using var _server = server;

        var grant = Assert.Single(transport.Grants);

        Assert.Equal(Guid.Parse(userId), grant.UserId);
        Assert.True(grant.Publish.Microphone);
        Assert.False(grant.Publish.Camera);
    }

    [Fact]
    public async Task Turning_a_camera_on_grants_it_at_the_media_server_and_tells_everybody()
    {
        var (server, transport, channelId, token, userId) = await InCallAsync();
        await using var _server = server;

        var updated = await new Proto.VoiceService.VoiceServiceClient(server.Channel).SetVoiceSelfStateAsync(
            new Proto.SetVoiceSelfStateRequest { CameraOn = true }, GrpcServer.As(token));

        Assert.True(updated.Participant.CameraOn);
        Assert.False(updated.Participant.CameraSuppressed);

        Assert.True(await transport.WaitForPushAsync(Guid.Parse(channelId), Guid.Parse(userId), true),
            "the camera grant never reached the media server");

        var allowed = transport.Allows(Guid.Parse(channelId), Guid.Parse(userId))!;
        Assert.True(allowed.Publish.Camera);
        Assert.True(allowed.Publish.Microphone);
    }

    [Fact]
    public async Task Turning_a_camera_off_takes_the_grant_away_again()
    {
        var (server, transport, channelId, token, userId) = await InCallAsync();
        await using var _server = server;
        var voice = new Proto.VoiceService.VoiceServiceClient(server.Channel);

        await voice.SetVoiceSelfStateAsync(new Proto.SetVoiceSelfStateRequest { CameraOn = true }, GrpcServer.As(token));
        Assert.True(await transport.WaitForPushAsync(Guid.Parse(channelId), Guid.Parse(userId), true));

        var off = await voice.SetVoiceSelfStateAsync(
            new Proto.SetVoiceSelfStateRequest { CameraOn = false }, GrpcServer.As(token));

        Assert.False(off.Participant.CameraOn);
        Assert.True(await transport.WaitForPushAsync(Guid.Parse(channelId), Guid.Parse(userId), false));
    }

    /// <summary>
    /// Mute and deafen are only icons and are always accepted; the camera is a decision and can be
    /// refused. A refused camera must leave the mute in the same request standing.
    /// </summary>
    [Fact]
    public async Task A_mute_in_the_same_request_still_applies_when_the_camera_is_refused()
    {
        var (server, _, _, token, _) = await InCallAsync(cameraCap: 0);
        await using var _server = server;
        var voice = new Proto.VoiceService.VoiceServiceClient(server.Channel);

        var refused = await Assert.ThrowsAsync<RpcException>(() => voice.SetVoiceSelfStateAsync(
            new Proto.SetVoiceSelfStateRequest { SelfMute = true, CameraOn = true }, GrpcServer.As(token)).ResponseAsync);

        Assert.Equal("missing_permission", ReasonOf(refused));

        var status = await voice.GetVoiceStatusAsync(new Proto.GetVoiceStatusRequest(), GrpcServer.As(token));
        var me = Assert.Single(status.Participants);
        Assert.True(me.SelfMute);
        Assert.False(me.CameraOn);
    }

    [Fact]
    public async Task A_server_that_carries_no_camera_video_refuses_one()
    {
        var (server, _, _, token, _) = await InCallAsync(cameraCap: 0);
        await using var _server = server;

        var status = await new Proto.VoiceService.VoiceServiceClient(server.Channel)
            .GetVoiceStatusAsync(new Proto.GetVoiceStatusRequest(), GrpcServer.As(token));

        // Suppressed from the moment they joined, so a client knows before it asks.
        Assert.Equal(0, status.MaximumCameraPublishers);
        Assert.True(Assert.Single(status.Participants).CameraSuppressed);
    }

    [Fact]
    public async Task A_member_without_the_video_permission_is_refused()
    {
        var transport = new FakeVoiceTransport();
        await using var server = await GrpcServer.StartAsync(voice: transport);

        var (owner, _) = await server.RegisterAsync();
        var channel = await new Proto.ChannelService.ChannelServiceClient(server.Channel).CreateChannelAsync(
            new Proto.CreateChannelRequest { Type = "voice", Name = "call" }, GrpcServer.As(owner));

        // Take `video` away from everyone, which is the only role an ordinary member holds.
        var roles = new Proto.RoleService.RoleServiceClient(server.Channel);
        var listed = await roles.ListRolesAsync(new Proto.ListRolesRequest(), GrpcServer.As(owner));
        var everyone = listed.Roles.Single(role => role.Kind == Proto.RoleKind.Everyone);

        var kept = new Proto.PermissionSet();
        kept.Permissions.AddRange(everyone.Permissions.Where(name => name != "video"));
        await roles.UpdateRoleAsync(
            new Proto.UpdateRoleRequest { RoleId = everyone.RoleId, Permissions = kept }, GrpcServer.As(owner));

        var (bob, _) = await server.RegisterAsync();
        var voice = new Proto.VoiceService.VoiceServiceClient(server.Channel);
        var joined = await voice.JoinVoiceAsync(
            new Proto.JoinVoiceRequest { ChannelId = channel.Channel.ChannelId }, GrpcServer.As(bob));

        Assert.True(joined.You.CameraSuppressed);
        Assert.False(joined.You.Suppressed);

        var refused = await Assert.ThrowsAsync<RpcException>(() => voice.SetVoiceSelfStateAsync(
            new Proto.SetVoiceSelfStateRequest { CameraOn = true }, GrpcServer.As(bob)).ResponseAsync);

        Assert.Equal("missing_permission", ReasonOf(refused));
        Assert.Equal(StatusCode.PermissionDenied, refused.StatusCode);
    }

    [Fact]
    public async Task The_camera_after_the_last_place_is_refused_as_camera_limit_reached()
    {
        var transport = new FakeVoiceTransport();
        await using var server = await GrpcServer.StartAsync(
            voice: transport, configureVoice: options => options.MaximumCameraPublishersPerChannel = 1);

        var (owner, _) = await server.RegisterAsync();
        var channel = await new Proto.ChannelService.ChannelServiceClient(server.Channel).CreateChannelAsync(
            new Proto.CreateChannelRequest { Type = "voice", Name = "call" }, GrpcServer.As(owner));

        var (bob, _) = await server.RegisterAsync();
        var voice = new Proto.VoiceService.VoiceServiceClient(server.Channel);

        foreach (var token in new[] { owner, bob })
            await voice.JoinVoiceAsync(
                new Proto.JoinVoiceRequest { ChannelId = channel.Channel.ChannelId }, GrpcServer.As(token));

        await voice.SetVoiceSelfStateAsync(new Proto.SetVoiceSelfStateRequest { CameraOn = true }, GrpcServer.As(owner));

        var refused = await Assert.ThrowsAsync<RpcException>(() => voice.SetVoiceSelfStateAsync(
            new Proto.SetVoiceSelfStateRequest { CameraOn = true }, GrpcServer.As(bob)).ResponseAsync);

        Assert.Equal("camera_limit_reached", ReasonOf(refused));
        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);

        // And the place frees up when the first camera goes off.
        await voice.SetVoiceSelfStateAsync(new Proto.SetVoiceSelfStateRequest { CameraOn = false }, GrpcServer.As(owner));
        var now = await voice.SetVoiceSelfStateAsync(
            new Proto.SetVoiceSelfStateRequest { CameraOn = true }, GrpcServer.As(bob));

        Assert.True(now.Participant.CameraOn);
    }

    [Fact]
    public async Task Turning_a_camera_on_without_being_in_a_call_is_not_in_voice()
    {
        var transport = new FakeVoiceTransport();
        await using var server = await GrpcServer.StartAsync(voice: transport);
        var (token, _) = await server.RegisterAsync();

        var refused = await Assert.ThrowsAsync<RpcException>(() =>
            new Proto.VoiceService.VoiceServiceClient(server.Channel).SetVoiceSelfStateAsync(
                new Proto.SetVoiceSelfStateRequest { CameraOn = true }, GrpcServer.As(token)).ResponseAsync);

        Assert.Equal("not_in_voice", ReasonOf(refused));
    }
}

/// <summary>
/// Screen sharing in a call, over the wire: the <c>screen_share</c> permission, the cap on how many
/// shares one call carries, the rule that a whole screen never carries sound, and the grant the media
/// server is given — including that a window's sound follows the right to speak, not the share.
/// </summary>
public class VoiceScreenShareGrpcTests
{
    private static string ReasonOf(RpcException exception) =>
        exception.GetRpcStatus()?.GetDetail<ErrorInfo>() is { Domain: RaindropInterceptor.ErrorDomain } info
            ? info.Reason
            : throw new Xunit.Sdk.XunitException($"No Raindrop ErrorInfo on {exception.Status}.");

    private static Proto.SetVoiceSelfStateRequest Window(bool audio, bool selfMute = false) => new()
    {
        SelfMute = selfMute,
        ScreenShareOn = true,
        ScreenShareKind = Proto.ScreenShareKind.Window,
        ScreenShareAudio = audio
    };

    /// <summary>A server with a voice transport, one voice channel, and the owner and Bob in the call.</summary>
    private static async Task<Call> CallAsync(int shareCap = 2)
    {
        var transport = new FakeVoiceTransport();
        var server = await GrpcServer.StartAsync(
            voice: transport,
            configureVoice: options => options.MaximumScreenSharesPerChannel = shareCap);

        var (owner, ownerId) = await server.RegisterAsync();
        var (bob, bobId) = await server.RegisterAsync();

        var channel = await new Proto.ChannelService.ChannelServiceClient(server.Channel).CreateChannelAsync(
            new Proto.CreateChannelRequest { Type = "voice", Name = $"call-{Guid.NewGuid():N}"[..20] },
            GrpcServer.As(owner));

        var voice = new Proto.VoiceService.VoiceServiceClient(server.Channel);
        foreach (var token in new[] { owner, bob })
            await voice.JoinVoiceAsync(
                new Proto.JoinVoiceRequest { ChannelId = channel.Channel.ChannelId }, GrpcServer.As(token));

        return new Call(server, transport, voice, Guid.Parse(channel.Channel.ChannelId),
            owner, Guid.Parse(ownerId), bob, Guid.Parse(bobId));
    }

    private sealed record Call(
        GrpcServer Server,
        FakeVoiceTransport Transport,
        Proto.VoiceService.VoiceServiceClient Voice,
        Guid ChannelId,
        string Owner,
        Guid OwnerId,
        string Bob,
        Guid BobId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Server.DisposeAsync();

        public async Task<Proto.VoiceParticipant> StateOf(Guid userId)
        {
            var status = await Voice.GetVoiceStatusAsync(new Proto.GetVoiceStatusRequest(), GrpcServer.As(Owner));
            return status.Participants.Single(participant => participant.UserId == userId.ToString("D"));
        }

        /// <summary>Polls until a participant's state satisfies <paramref name="wanted"/>; re-evaluation is queued.</summary>
        public async Task<bool> WaitForStateAsync(Guid userId, Func<Proto.VoiceParticipant, bool> wanted)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (wanted(await StateOf(userId)))
                    return true;

                await Task.Delay(20);
            }

            return false;
        }
    }

    [Fact]
    public async Task A_call_reports_how_many_screen_shares_it_carries()
    {
        await using var call = await CallAsync(shareCap: 3);

        var status = await call.Voice.GetVoiceStatusAsync(new Proto.GetVoiceStatusRequest(), GrpcServer.As(call.Owner));

        Assert.Equal(3, status.MaximumScreenShares);
    }

    /// <summary>Joining never starts a share, so no join token carries one.</summary>
    [Fact]
    public async Task Joining_grants_no_screen_share()
    {
        await using var call = await CallAsync();

        Assert.All(call.Transport.Grants, grant =>
        {
            Assert.False(grant.Publish.Screen);
            Assert.False(grant.Publish.ScreenAudio);
        });
    }

    [Fact]
    public async Task Sharing_a_window_with_sound_grants_both_at_the_media_server_and_tells_everybody()
    {
        await using var call = await CallAsync();

        var shared = await call.Voice.SetVoiceSelfStateAsync(Window(audio: true), GrpcServer.As(call.Bob));

        Assert.True(shared.Participant.ScreenShareOn);
        Assert.Equal(Proto.ScreenShareKind.Window, shared.Participant.ScreenShareKind);
        Assert.True(shared.Participant.ScreenShareAudio);
        Assert.True(await call.Transport.WaitForGrantAsync(call.ChannelId, call.BobId,
            grant => grant is { Screen: true, ScreenAudio: true, Microphone: true }),
            "the share's grant never reached the media server");

        // Everybody else sees it in the roster.
        var seen = await call.StateOf(call.BobId);
        Assert.True(seen.ScreenShareOn);
    }

    [Fact]
    public async Task A_whole_screen_is_granted_its_picture_and_never_sound()
    {
        await using var call = await CallAsync();

        var shared = await call.Voice.SetVoiceSelfStateAsync(new Proto.SetVoiceSelfStateRequest
        {
            ScreenShareOn = true, ScreenShareKind = Proto.ScreenShareKind.Screen
        }, GrpcServer.As(call.Bob));

        Assert.Equal(Proto.ScreenShareKind.Screen, shared.Participant.ScreenShareKind);
        Assert.False(shared.Participant.ScreenShareAudio);
        Assert.True(await call.Transport.WaitForGrantAsync(call.ChannelId, call.BobId,
            grant => grant is { Screen: true, ScreenAudio: false }));
    }

    [Fact]
    public async Task Asking_for_a_screens_sound_or_a_share_of_nothing_is_refused_outright()
    {
        await using var call = await CallAsync();

        var screenSound = await Assert.ThrowsAsync<RpcException>(() => call.Voice.SetVoiceSelfStateAsync(
            new Proto.SetVoiceSelfStateRequest
            {
                SelfMute = true,
                ScreenShareOn = true,
                ScreenShareKind = Proto.ScreenShareKind.Screen,
                ScreenShareAudio = true
            }, GrpcServer.As(call.Bob)).ResponseAsync);

        Assert.Equal("invalid_payload", ReasonOf(screenSound));
        Assert.Equal(StatusCode.InvalidArgument, screenSound.StatusCode);

        var noKind = await Assert.ThrowsAsync<RpcException>(() => call.Voice.SetVoiceSelfStateAsync(
            new Proto.SetVoiceSelfStateRequest { ScreenShareOn = true }, GrpcServer.As(call.Bob)).ResponseAsync);

        Assert.Equal("invalid_payload", ReasonOf(noKind));

        // A malformed request is a client bug, not a decision: none of it is applied, the mute included.
        var bob = await call.StateOf(call.BobId);
        Assert.False(bob.ScreenShareOn);
        Assert.False(bob.SelfMute);
    }

    /// <summary>
    /// The request is the whole state. A mute toggle that carries the share it already has leaves the
    /// share — and the window's sound, which is not their microphone — exactly as it was; one that
    /// leaves the share out ends it.
    /// </summary>
    [Fact]
    public async Task A_mute_that_carries_the_share_leaves_it_running_and_one_that_does_not_ends_it()
    {
        await using var call = await CallAsync();
        await call.Voice.SetVoiceSelfStateAsync(Window(audio: true), GrpcServer.As(call.Bob));

        var muted = await call.Voice.SetVoiceSelfStateAsync(Window(audio: true, selfMute: true), GrpcServer.As(call.Bob));

        Assert.True(muted.Participant.SelfMute);
        Assert.True(muted.Participant.ScreenShareOn);
        Assert.True(muted.Participant.ScreenShareAudio);
        Assert.True(await call.Transport.WaitForGrantAsync(call.ChannelId, call.BobId,
            grant => grant is { Screen: true, ScreenAudio: true }));

        var dropped = await call.Voice.SetVoiceSelfStateAsync(
            new Proto.SetVoiceSelfStateRequest { SelfMute = false }, GrpcServer.As(call.Bob));

        Assert.False(dropped.Participant.ScreenShareOn);
        Assert.Equal(Proto.ScreenShareKind.Unspecified, dropped.Participant.ScreenShareKind);
        Assert.True(await call.Transport.WaitForGrantAsync(call.ChannelId, call.BobId,
            grant => grant is { Screen: false, ScreenAudio: false }));
    }

    /// <summary>
    /// A mute for everyone silences a shared window as well as a voice, leaves the picture, and the
    /// sound does not come back by itself when the mute is lifted.
    /// </summary>
    [Fact]
    public async Task A_mute_for_everyone_takes_the_windows_sound_and_leaves_its_picture()
    {
        await using var call = await CallAsync();
        await call.Voice.SetVoiceSelfStateAsync(Window(audio: true), GrpcServer.As(call.Bob));

        await call.Voice.MuteVoiceMemberAsync(
            new Proto.MuteVoiceMemberRequest { UserId = call.BobId.ToString("D"), Muted = true },
            GrpcServer.As(call.Owner));

        var bob = await call.StateOf(call.BobId);
        Assert.True(bob.ServerMute);
        Assert.True(bob.ScreenShareOn);
        Assert.False(bob.ScreenShareAudio);
        Assert.True(await call.Transport.WaitForGrantAsync(call.ChannelId, call.BobId,
            grant => grant is { Microphone: false, Screen: true, ScreenAudio: false }));

        await call.Voice.MuteVoiceMemberAsync(
            new Proto.MuteVoiceMemberRequest { UserId = call.BobId.ToString("D"), Muted = false },
            GrpcServer.As(call.Owner));

        Assert.False((await call.StateOf(call.BobId)).ScreenShareAudio);
    }

    [Fact]
    public async Task Asking_for_a_windows_sound_while_muted_for_everyone_is_refused()
    {
        await using var call = await CallAsync();
        await call.Voice.MuteVoiceMemberAsync(
            new Proto.MuteVoiceMemberRequest { UserId = call.BobId.ToString("D"), Muted = true },
            GrpcServer.As(call.Owner));

        var refused = await Assert.ThrowsAsync<RpcException>(() =>
            call.Voice.SetVoiceSelfStateAsync(Window(audio: true), GrpcServer.As(call.Bob)).ResponseAsync);

        Assert.Equal("missing_permission", ReasonOf(refused));

        // The picture alone is fine.
        var silent = await call.Voice.SetVoiceSelfStateAsync(Window(audio: false), GrpcServer.As(call.Bob));
        Assert.True(silent.Participant.ScreenShareOn);
    }

    [Fact]
    public async Task The_share_after_the_last_place_is_refused_as_screen_share_limit_reached()
    {
        await using var call = await CallAsync(shareCap: 1);
        await call.Voice.SetVoiceSelfStateAsync(Window(audio: true), GrpcServer.As(call.Owner));

        var refused = await Assert.ThrowsAsync<RpcException>(() =>
            call.Voice.SetVoiceSelfStateAsync(Window(audio: false), GrpcServer.As(call.Bob)).ResponseAsync);

        Assert.Equal("screen_share_limit_reached", ReasonOf(refused));
        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);

        // The sharer changing what they carry is not a second share, even in a full call.
        var changed = await call.Voice.SetVoiceSelfStateAsync(Window(audio: false), GrpcServer.As(call.Owner));
        Assert.False(changed.Participant.ScreenShareAudio);

        // And the place frees up when the first share stops.
        await call.Voice.SetVoiceSelfStateAsync(new Proto.SetVoiceSelfStateRequest(), GrpcServer.As(call.Owner));
        var now = await call.Voice.SetVoiceSelfStateAsync(Window(audio: false), GrpcServer.As(call.Bob));
        Assert.True(now.Participant.ScreenShareOn);
    }

    [Fact]
    public async Task A_server_that_carries_no_screen_sharing_refuses_one()
    {
        await using var call = await CallAsync(shareCap: 0);

        var status = await call.Voice.GetVoiceStatusAsync(new Proto.GetVoiceStatusRequest(), GrpcServer.As(call.Owner));
        Assert.Equal(0, status.MaximumScreenShares);
        Assert.All(status.Participants, participant => Assert.True(participant.ScreenShareSuppressed));

        var refused = await Assert.ThrowsAsync<RpcException>(() =>
            call.Voice.SetVoiceSelfStateAsync(Window(audio: false), GrpcServer.As(call.Owner)).ResponseAsync);
        Assert.Equal("missing_permission", ReasonOf(refused));
    }

    /// <summary>
    /// Taking <c>screen_share</c> away mid-call ends a running share: the grant the media server
    /// holds stops accepting it, and the roster stops claiming a share nobody can see. Somebody who
    /// joins afterwards is suppressed from the start.
    /// </summary>
    [Fact]
    public async Task Losing_screen_share_mid_call_ends_the_share()
    {
        await using var call = await CallAsync();
        await call.Voice.SetVoiceSelfStateAsync(Window(audio: true), GrpcServer.As(call.Bob));

        var roles = new Proto.RoleService.RoleServiceClient(call.Server.Channel);
        var listed = await roles.ListRolesAsync(new Proto.ListRolesRequest(), GrpcServer.As(call.Owner));
        var everyone = listed.Roles.Single(role => role.Kind == Proto.RoleKind.Everyone);

        var kept = new Proto.PermissionSet();
        kept.Permissions.AddRange(everyone.Permissions.Where(name => name != "screen_share"));
        await roles.UpdateRoleAsync(
            new Proto.UpdateRoleRequest { RoleId = everyone.RoleId, Permissions = kept }, GrpcServer.As(call.Owner));

        Assert.True(await call.WaitForStateAsync(call.BobId,
            bob => bob is { ScreenShareOn: false, ScreenShareSuppressed: true, ScreenShareAudio: false }),
            "losing screen_share never ended the share");
        Assert.True(await call.Transport.WaitForGrantAsync(call.ChannelId, call.BobId,
            grant => grant is { Screen: false, ScreenAudio: false }));

        var refused = await Assert.ThrowsAsync<RpcException>(() =>
            call.Voice.SetVoiceSelfStateAsync(Window(audio: false), GrpcServer.As(call.Bob)).ResponseAsync);
        Assert.Equal("missing_permission", ReasonOf(refused));
    }
}

