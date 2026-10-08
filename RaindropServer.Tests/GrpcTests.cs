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

    public static async Task<GrpcServer> StartAsync(Action<EventOptions>? events = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"raindrop-grpc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);

        var services = new ServiceCollection();
        services.AddRaindropServer(
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
