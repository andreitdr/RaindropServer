using System.Security.Cryptography;
using RaindropServer.Attachments;
using RaindropServer.Channels;
using RaindropServer.Channels.Text;
using RaindropServer.Configuration;
using RaindropServer.Roles;
using RaindropServer.Users;
using ServerCli;

namespace RaindropServer.Tests;

/// <summary>
/// The length contract with the Rust core's <c>raindrop/attachment/v1</c> format — the numbers here
/// are the ones <c>attachment::tests::length_formula_inverts_exactly</c> asserts on the other side.
/// </summary>
public class AttachmentFormatTests
{
    [Theory]
    [InlineData(0, 36)]
    [InlineData(1, 37)]
    [InlineData(65_536, 20 + 65_536 + 16)]
    [InlineData(65_537, 20 + 65_537 + 32)]
    [InlineData(3 * 65_536 + 17, 20 + 3 * 65_536 + 17 + 4 * 16)]
    public void The_ciphertext_length_matches_the_rust_core(long plaintext, long ciphertext)
    {
        Assert.Equal(ciphertext, AttachmentFormat.CiphertextLength(plaintext));
        Assert.Equal(plaintext, AttachmentFormat.PlaintextLength(ciphertext));
    }

    [Fact]
    public void Every_plaintext_length_round_trips()
    {
        for (long plaintext = 0; plaintext < 400_000; plaintext += 991)
            Assert.Equal(plaintext, AttachmentFormat.PlaintextLength(AttachmentFormat.CiphertextLength(plaintext)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(35)]
    [InlineData(20 + 65_552 + 1)]
    [InlineData(20 + 65_552 + 16)]
    [InlineData(-5)]
    public void A_length_no_file_encrypts_to_is_refused(long ciphertext) =>
        Assert.Null(AttachmentFormat.PlaintextLength(ciphertext));
}

/// <summary>What <c>attachments</c> in <c>raindrop.json</c> may say, and what an owner is told when it is wrong.</summary>
public class AttachmentConfigurationTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "raindrop-config-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static ConfigurationReport Check(AttachmentOptions options)
    {
        var report = new ConfigurationReport();
        options.Validate(report.Section("attachments"));
        return report;
    }

    private (ServerConfig Config, ConfigurationReport Report) Read(string json)
    {
        var path = Path.Combine(_directory, "raindrop.json");
        File.WriteAllText(path, json);

        var report = new ConfigurationReport();
        var config = ServerConfig.Load(path, report);
        config.Validate(report);
        return (config, report);
    }

    private static ConfigurationProblem Only(ConfigurationReport report, string setting) =>
        Assert.Single(report.Problems, problem => problem.Setting == setting);

    [Fact]
    public void The_defaults_are_clean() =>
        Assert.False(Check(new AttachmentOptions()).HasProblems, Check(new AttachmentOptions()).Describe());

    [Theory]
    [InlineData(".png", "\"png\"")]
    [InlineData("PNG", "\"png\"")]
    [InlineData("tar.gz", "without the dot")]
    public void A_malformed_extension_is_an_error_saying_how_to_write_it(string written, string remedy)
    {
        var problem = Only(Check(new AttachmentOptions { AllowedExtensions = [written] }), "attachments.allowedExtensions");

        Assert.Equal(ConfigurationSeverity.Error, problem.Severity);
        Assert.Contains(remedy, problem.Remedy);
    }

    [Fact]
    public void An_extension_both_allowed_and_blocked_is_a_contradiction()
    {
        var problem = Only(Check(new AttachmentOptions { AllowedExtensions = ["png", "exe"] }), "attachments.allowedExtensions");

        Assert.Equal(ConfigurationSeverity.Error, problem.Severity);
        Assert.Contains("'exe'", problem.Message);
    }

    [Fact]
    public void A_size_limit_for_a_type_that_is_never_accepted_is_an_error()
    {
        var options = new AttachmentOptions();
        options.MaximumFileBytesByExtension["exe"] = 100 * AttachmentOptions.Megabyte;

        Assert.Equal(ConfigurationSeverity.Error,
            Only(Check(options), "attachments.maximumFileSizeMegabytesByExtension.exe").Severity);
    }

    [Fact]
    public void Previewing_a_type_that_is_never_accepted_is_a_warning()
    {
        var options = new AttachmentOptions { AllowedExtensions = ["png"], PreviewExtensions = ["png", "mp4"] };

        var problem = Only(Check(options), "attachments.preview.extensions");
        Assert.Equal(ConfigurationSeverity.Warning, problem.Severity);
        Assert.Contains("'mp4'", problem.Message);
    }

    [Fact]
    public void Accepting_nothing_at_all_is_an_error() =>
        Assert.True(Check(new AttachmentOptions { AllowedExtensions = [], AllowFilesWithoutExtension = false }).HasErrors);

    [Theory]
    [InlineData(0)]
    [InlineData(2L * 1024 * 1024 * 1024 * 1024 * 1024)]
    public void A_file_limit_outside_a_megabyte_to_a_terabyte_is_an_error(long bytes) =>
        Assert.True(Check(new AttachmentOptions { MaximumFileBytes = bytes }).HasErrors);

    [Fact]
    public void Fewer_pending_uploads_than_one_message_carries_is_a_contradiction() =>
        Assert.Equal(ConfigurationSeverity.Error, Only(
            Check(new AttachmentOptions { MaximumFilesPerMessage = 10, MaximumPendingUploadsPerMember = 5 }),
            "attachments.maximumPendingUploadsPerMember").Severity);

    [Fact]
    public void The_file_is_read_in_megabytes_with_per_type_limits()
    {
        var (config, report) = Read("""
            {
              "attachments": {
                "maximumFileSizeMegabytes": 50,
                "maximumFileSizeMegabytesByExtension": { "mp4": 500 },
                "allowedExtensions": [ "png", "mp4" ],
                "preview": { "extensions": [ "png", "mp4" ], "autoLoadMaximumMegabytes": 5 }
              }
            }
            """);

        Assert.False(report.HasErrors, report.Describe());

        var options = new AttachmentOptions();
        config.ApplyTo(options);

        Assert.Equal(50 * AttachmentOptions.Megabyte, options.MaximumBytesFor("png"));
        Assert.Equal(500 * AttachmentOptions.Megabyte, options.MaximumBytesFor("mp4"));
        Assert.Equal(5 * AttachmentOptions.Megabyte, options.PreviewAutoLoadMaximumBytes);
        Assert.False(options.IsTypeAllowed("zip"));
        Assert.False(options.IsTypeAllowed("exe"));
    }

    [Fact]
    public void A_size_map_keeps_its_keys_but_still_checks_its_values_and_the_names_around_it()
    {
        var (_, report) = Read("""
            {
              "attachments": {
                "maximumFileSizeMegabyte": 50,
                "maximumFileSizeMegabytesByExtension": { "mp4": "big", "zip": 100 }
              }
            }
            """);

        Assert.Contains("maximumFileSizeMegabytes", Only(report, "attachments.maximumFileSizeMegabyte").Remedy);
        Assert.Contains("whole number", Only(report, "attachments.maximumFileSizeMegabytesByExtension.mp4").Message);
        Assert.DoesNotContain(report.Problems, problem => problem.Setting.EndsWith(".zip"));
    }

    [Fact]
    public void The_shipped_blocklist_applies_unless_the_file_names_its_own()
    {
        var (defaults, _) = Read("""{ "attachments": { "enabled": true } }""");
        var (none, _) = Read("""{ "attachments": { "blockedExtensions": [] } }""");

        var options = new AttachmentOptions();
        defaults.ApplyTo(options);
        Assert.False(options.IsTypeAllowed("exe"));

        none.ApplyTo(options);
        Assert.True(options.IsTypeAllowed("exe"));
    }
}

/// <summary>The attachment store on every database, including the races its conditional updates settle.</summary>
public class AttachmentStoreTests
{
    private static AttachmentRecord Uploaded(Guid uploader, Guid channel, string kind = AttachmentNames.FileKind) => new()
    {
        AttachmentId = Guid.NewGuid(),
        ChannelId = channel,
        UploaderId = uploader,
        UploaderDeviceId = Guid.NewGuid(),
        Kind = kind,
        Extension = "png",
        CiphertextSize = 36,
        ReceivedSize = 36,
        CiphertextSha256 = new byte[32],
        State = AttachmentState.Uploaded,
        CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1)
    };

    [Theory]
    [MemberData(nameof(TestDatabases.Providers), MemberType = typeof(TestDatabases))]
    public async Task Two_messages_racing_for_one_file_cannot_both_have_it(string provider)
    {
        var (driver, options) = await TestDatabases.OpenAsync(provider);
        var store = driver.CreateAttachmentStore(options);
        var (uploader, channel) = (Guid.NewGuid(), Guid.NewGuid());
        var file = Uploaded(uploader, channel);
        await store.InsertAsync(file, default);

        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            store.ClaimAsync([file.AttachmentId], Guid.NewGuid(), uploader, channel, default)));

        Assert.Equal(1, claims.Count(won => won));
        Assert.Equal(AttachmentState.Attached, (await store.FindAsync(file.AttachmentId, default))!.State);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Providers), MemberType = typeof(TestDatabases))]
    public async Task A_claim_is_all_or_nothing_and_only_for_the_uploader_in_its_channel(string provider)
    {
        var (driver, options) = await TestDatabases.OpenAsync(provider);
        var store = driver.CreateAttachmentStore(options);
        var (uploader, channel) = (Guid.NewGuid(), Guid.NewGuid());
        var first = Uploaded(uploader, channel);
        var elsewhere = Uploaded(uploader, Guid.NewGuid());
        await store.InsertAsync(first, default);
        await store.InsertAsync(elsewhere, default);

        Assert.False(await store.ClaimAsync([first.AttachmentId, elsewhere.AttachmentId], Guid.NewGuid(), uploader, channel, default));
        Assert.Equal(AttachmentState.Uploaded, (await store.FindAsync(first.AttachmentId, default))!.State);

        Assert.False(await store.ClaimAsync([first.AttachmentId], Guid.NewGuid(), Guid.NewGuid(), channel, default));

        var message = Guid.NewGuid();
        Assert.True(await store.ClaimAsync([first.AttachmentId], message, uploader, channel, default));
        Assert.Single(await store.ListForMessagesAsync([message], default));

        await store.ReleaseAsync(message, default);
        Assert.Empty(await store.ListForMessagesAsync([message], default));
        Assert.Equal(AttachmentState.Uploaded, (await store.FindAsync(first.AttachmentId, default))!.State);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Providers), MemberType = typeof(TestDatabases))]
    public async Task Uploads_advance_in_order_and_complete_once(string provider)
    {
        var (driver, options) = await TestDatabases.OpenAsync(provider);
        var store = driver.CreateAttachmentStore(options);
        var upload = Uploaded(Guid.NewGuid(), Guid.NewGuid()) with
        {
            State = AttachmentState.Uploading, ReceivedSize = 0, CiphertextSha256 = null, CiphertextSize = 100
        };
        await store.InsertAsync(upload, default);

        Assert.True(await store.UpdateReceivedAsync(upload.AttachmentId, 0, 60, default));
        Assert.False(await store.UpdateReceivedAsync(upload.AttachmentId, 0, 60, default));
        Assert.True(await store.UpdateReceivedAsync(upload.AttachmentId, 60, 100, default));

        var hash = RandomNumberGenerator.GetBytes(32);
        Assert.True(await store.CompleteAsync(upload.AttachmentId, hash, DateTime.UtcNow, default));
        Assert.False(await store.CompleteAsync(upload.AttachmentId, hash, DateTime.UtcNow, default));

        var stored = (await store.FindAsync(upload.AttachmentId, default))!;
        Assert.Equal(AttachmentState.Uploaded, stored.State);
        Assert.Equal(100, stored.ReceivedSize);
        Assert.Equal(hash, stored.CiphertextSha256);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Providers), MemberType = typeof(TestDatabases))]
    public async Task Deleting_a_channel_or_expiring_uploads_leaves_them_for_removal(string provider)
    {
        var (driver, options) = await TestDatabases.OpenAsync(provider);
        var store = driver.CreateAttachmentStore(options);
        var (uploader, channel) = (Guid.NewGuid(), Guid.NewGuid());
        var sent = Uploaded(uploader, channel);
        var stale = Uploaded(uploader, Guid.NewGuid()) with { CreatedAtUtc = DateTime.UtcNow.AddDays(-3) };
        await store.InsertAsync(sent, default);
        await store.InsertAsync(stale, default);
        await store.ClaimAsync([sent.AttachmentId], Guid.NewGuid(), uploader, channel, default);

        Assert.Equal(1, await store.CountPendingAsync(uploader, default));

        await store.MarkChannelDeletedAsync(channel, DateTime.UtcNow, default);
        Assert.Equal(1, await store.ExpirePendingAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, default));

        var deleted = await store.ListDeletedAsync(100, default);
        Assert.Contains(deleted, attachment => attachment.AttachmentId == sent.AttachmentId);
        Assert.Contains(deleted, attachment => attachment.AttachmentId == stale.AttachmentId);
        Assert.Equal(0, await store.CountPendingAsync(uploader, default));

        await store.RemoveAsync(sent.AttachmentId, default);
        Assert.Null(await store.FindAsync(sent.AttachmentId, default));
    }
}

/// <summary>Permissions a test sets per member, instead of roles.</summary>
public sealed class FixedPermissions : IPermissionResolver
{
    public Dictionary<Guid, Permission> Granted { get; } = [];

    public Task<Permission> GetAsync(Guid userId, ChannelRecord? channel, CancellationToken cancellationToken) =>
        Task.FromResult(Granted.GetValueOrDefault(userId));
}

/// <summary>
/// The upload, attach and download rules end to end, over a real store and a real folder — with bytes
/// that stand in for ciphertext, since the server never looks inside it.
/// </summary>
public class AttachmentServiceTests : IDisposable
{
    private const Permission Member = Permission.ViewChannel | Permission.SendMessages | Permission.AttachFiles;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "raindrop-attachments-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private sealed record Setup(
        AttachmentService Attachments,
        IAttachmentStore Store,
        FixedPermissions Permissions,
        ChannelRecord Channel,
        AttachmentOptions Options,
        AttachmentMaintenanceService Maintenance);

    private async Task<Setup> CreateAsync(Action<AttachmentOptions>? configure = null)
    {
        var (driver, options) = await TestDatabases.OpenAsync("sqlite");
        var channels = driver.CreateChannelStore(options);
        var store = driver.CreateAttachmentStore(options);
        var permissions = new FixedPermissions();
        var attachmentOptions = new AttachmentOptions { StoragePath = _folder, UploadChunkBytes = 64 };
        configure?.Invoke(attachmentOptions);

        var channelService = new ChannelService(channels, permissions, [], new RecordingPublisher());
        var storage = new LocalFolderAttachmentStorage(attachmentOptions);
        var service = new AttachmentService(channelService, store, storage,
            [new ConfiguredAttachmentPolicy(attachmentOptions, store)], [], attachmentOptions);
        var maintenance = new AttachmentMaintenanceService(store, storage, [], attachmentOptions);

        return new Setup(service, store, permissions, await Sessions.ChannelAsync(channels, Guid.NewGuid()),
            attachmentOptions, maintenance);
    }

    private static UserSession MemberOf(Setup setup, Permission granted = Member)
    {
        var session = Sessions.For(Guid.NewGuid(), Guid.NewGuid());
        setup.Permissions.Granted[session.UserId] = granted;
        return session;
    }

    private static async Task<string> CodeOf(Func<Task> call) =>
        (await Assert.ThrowsAsync<ApiException>(call)).Code;

    /// <summary>Uploads <paramref name="ciphertext"/> in chunks and finishes it.</summary>
    private static async Task<Guid> UploadAsync(Setup setup, UserSession session, byte[] ciphertext, string extension = "png")
    {
        var upload = await setup.Attachments.CreateUploadAsync(session,
            new CreateUploadRequest(setup.Channel.ChannelId, extension, ciphertext.Length), default);

        for (var offset = 0; offset < ciphertext.Length; offset += upload.ChunkSize)
        {
            var chunk = ciphertext.AsSpan(offset, Math.Min(upload.ChunkSize, ciphertext.Length - offset)).ToArray();
            await setup.Attachments.WriteChunkAsync(session, upload.AttachmentId, offset, new MemoryStream(chunk), default);
        }

        await setup.Attachments.CompleteAsync(session,
            new CompleteUploadRequest(upload.AttachmentId, SHA256.HashData(ciphertext)), default);

        return upload.AttachmentId;
    }

    private static byte[] Ciphertext(long plaintext) =>
        RandomNumberGenerator.GetBytes((int)AttachmentFormat.CiphertextLength(plaintext));

    private static ChannelAccess AccessOf(Setup setup) => new(setup.Channel, Member);

    [Fact]
    public async Task A_file_goes_up_in_chunks_is_sent_and_comes_back_to_anybody_who_can_see_the_channel()
    {
        var setup = await CreateAsync();
        var author = MemberOf(setup);
        var reader = MemberOf(setup, Permission.ViewChannel);
        var outsider = MemberOf(setup, Permission.None);
        var ciphertext = Ciphertext(300);

        var id = await UploadAsync(setup, author, ciphertext);

        // Before it is sent, only its uploader can fetch it.
        Assert.Equal("attachment_not_found", await CodeOf(() => setup.Attachments.OpenDownloadAsync(reader, id, default)));

        var message = Guid.NewGuid();
        var attached = await setup.Attachments.AttachAsync(author, AccessOf(setup), message, [id], default);
        Assert.Equal("png", Assert.Single(attached).Extension);

        var (_, content) = await setup.Attachments.OpenDownloadAsync(reader, id, default);
        await using (content)
        {
            var copy = new MemoryStream();
            await content.CopyToAsync(copy);
            Assert.Equal(ciphertext, copy.ToArray());
        }

        Assert.Equal("attachment_not_found", await CodeOf(() => setup.Attachments.OpenDownloadAsync(outsider, id, default)));

        var listed = await setup.Attachments.ListAsync([message], default);
        Assert.Equal(id, Assert.Single(listed[message]).AttachmentId);
    }

    [Fact]
    public async Task The_owner_s_rules_are_applied_to_what_the_upload_declares()
    {
        var setup = await CreateAsync(options =>
        {
            options.MaximumFileBytes = AttachmentOptions.Megabyte;
            options.MaximumFileBytesByExtension["mp4"] = 2 * AttachmentOptions.Megabyte;
        });
        var member = MemberOf(setup);
        var channel = setup.Channel.ChannelId;

        Task Create(string extension, long plaintext) => setup.Attachments.CreateUploadAsync(member,
            new CreateUploadRequest(channel, extension, AttachmentFormat.CiphertextLength(plaintext)), default);

        Assert.Equal("file_type_not_allowed", await CodeOf(() => Create("exe", 10)));
        Assert.Equal("file_too_large", await CodeOf(() => Create("png", AttachmentOptions.Megabyte + 1)));
        await Create("mp4", AttachmentOptions.Megabyte + 1);
        await Create(".PNG", 10);

        Assert.Equal("invalid_attachment", await CodeOf(() => setup.Attachments.CreateUploadAsync(member,
            new CreateUploadRequest(channel, "png", 35), default)));
        Assert.Equal("invalid_attachment", await CodeOf(() => Create("a/b", 10)));
    }

    [Fact]
    public async Task Uploading_needs_a_bound_session_a_visible_channel_and_attach_files()
    {
        var setup = await CreateAsync();
        var request = new CreateUploadRequest(setup.Channel.ChannelId, "png", 36);

        Assert.Equal("device_required", await CodeOf(() =>
            setup.Attachments.CreateUploadAsync(MemberOf(setup) with { DeviceId = null }, request, default)));
        Assert.Equal("channel_not_found", await CodeOf(() =>
            setup.Attachments.CreateUploadAsync(MemberOf(setup, Permission.None), request, default)));
        Assert.Equal("missing_permission", await CodeOf(() =>
            setup.Attachments.CreateUploadAsync(MemberOf(setup, Permission.ViewChannel | Permission.SendMessages), request, default)));
    }

    [Fact]
    public async Task Chunks_arrive_in_order_within_the_file_and_the_chunk_size()
    {
        var setup = await CreateAsync();
        var member = MemberOf(setup);
        var upload = await setup.Attachments.CreateUploadAsync(member,
            new CreateUploadRequest(setup.Channel.ChannelId, "png", AttachmentFormat.CiphertextLength(100)), default);

        Assert.Equal("upload_offset_mismatch", await CodeOf(() =>
            setup.Attachments.WriteChunkAsync(member, upload.AttachmentId, 10, new MemoryStream(new byte[10]), default)));
        Assert.Equal("chunk_too_large", await CodeOf(() =>
            setup.Attachments.WriteChunkAsync(member, upload.AttachmentId, 0, new MemoryStream(new byte[65]), default)));

        // A chunk that failed leaves nothing behind: the next one starts from the same offset.
        var status = await setup.Attachments.WriteChunkAsync(member, upload.AttachmentId, 0, new MemoryStream(new byte[64]), default);
        Assert.Equal(64, status.ReceivedSize);

        Assert.Equal("upload_incomplete", await CodeOf(() => setup.Attachments.CompleteAsync(member,
            new CompleteUploadRequest(upload.AttachmentId, new byte[32]), default)));

        Assert.Equal("attachment_not_found", await CodeOf(() =>
            setup.Attachments.WriteChunkAsync(MemberOf(setup), upload.AttachmentId, 64, new MemoryStream(new byte[10]), default)));
    }

    [Fact]
    public async Task A_file_that_does_not_hash_to_what_was_encrypted_is_thrown_away()
    {
        var setup = await CreateAsync();
        var member = MemberOf(setup);
        var ciphertext = Ciphertext(10);
        var upload = await setup.Attachments.CreateUploadAsync(member,
            new CreateUploadRequest(setup.Channel.ChannelId, "png", ciphertext.Length), default);
        await setup.Attachments.WriteChunkAsync(member, upload.AttachmentId, 0, new MemoryStream(ciphertext), default);

        Assert.Equal("hash_mismatch", await CodeOf(() => setup.Attachments.CompleteAsync(member,
            new CompleteUploadRequest(upload.AttachmentId, new byte[32]), default)));

        Assert.Equal(AttachmentState.Deleted, (await setup.Store.FindAsync(upload.AttachmentId, default))!.State);
    }

    [Fact]
    public async Task A_message_carries_only_its_author_s_finished_files_once_and_within_the_limit()
    {
        var setup = await CreateAsync(options => options.MaximumFilesPerMessage = 2);
        var author = MemberOf(setup);
        var files = new List<Guid>();
        for (var i = 0; i < 3; i++)
            files.Add(await UploadAsync(setup, author, Ciphertext(5)));

        Assert.Equal("too_many_attachments", await CodeOf(() =>
            setup.Attachments.AttachAsync(author, AccessOf(setup), Guid.NewGuid(), files, default)));
        Assert.Equal("attachment_not_found", await CodeOf(() =>
            setup.Attachments.AttachAsync(MemberOf(setup), AccessOf(setup), Guid.NewGuid(), [files[0]], default)));
        Assert.Equal("missing_permission", await CodeOf(() => setup.Attachments.AttachAsync(author,
            new ChannelAccess(setup.Channel, Permission.ViewChannel | Permission.SendMessages), Guid.NewGuid(), [files[0]], default)));

        await setup.Attachments.AttachAsync(author, AccessOf(setup), Guid.NewGuid(), [files[0], files[1]], default);
        Assert.Equal("attachment_already_sent", await CodeOf(() =>
            setup.Attachments.AttachAsync(author, AccessOf(setup), Guid.NewGuid(), [files[0]], default)));

        var pending = await setup.Attachments.CreateUploadAsync(author,
            new CreateUploadRequest(setup.Channel.ChannelId, "png", 36), default);
        Assert.Equal("upload_incomplete", await CodeOf(() =>
            setup.Attachments.AttachAsync(author, AccessOf(setup), Guid.NewGuid(), [pending.AttachmentId], default)));
    }

    [Fact]
    public async Task Too_many_waiting_uploads_are_refused()
    {
        var setup = await CreateAsync(options =>
        {
            options.MaximumPendingUploadsPerMember = 2;
            options.MaximumFilesPerMessage = 1;
        });
        var member = MemberOf(setup);
        var request = new CreateUploadRequest(setup.Channel.ChannelId, "png", 36);

        await setup.Attachments.CreateUploadAsync(member, request, default);
        await setup.Attachments.CreateUploadAsync(member, request, default);

        Assert.Equal("too_many_pending_uploads", await CodeOf(() => setup.Attachments.CreateUploadAsync(member, request, default)));
    }

    [Fact]
    public async Task Deleting_the_message_removes_its_file_from_storage()
    {
        var setup = await CreateAsync();
        var author = MemberOf(setup);
        var id = await UploadAsync(setup, author, Ciphertext(40));
        var message = Guid.NewGuid();
        await setup.Attachments.AttachAsync(author, AccessOf(setup), message, [id], default);

        var listener = new AttachmentDeletionListener(setup.Store);
        await listener.OnMessageDeletingAsync(new MessageDeletion(
            new TextMessageRecord
            {
                MessageId = message, ChannelId = setup.Channel.ChannelId, AuthorId = author.UserId,
                SentAtUtc = DateTime.UtcNow, Epoch = 1, Ciphertext = [1]
            },
            setup.Channel, author.UserId, DateTime.UtcNow), default);

        Assert.Equal(1, await setup.Maintenance.SweepAsync(default));
        Assert.Null(await setup.Store.FindAsync(id, default));
        Assert.Empty(Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_stored_file_with_no_record_is_cleared_away_once_it_is_old_enough()
    {
        var setup = await CreateAsync();
        var orphan = Guid.NewGuid().ToString("N");
        var path = Path.Combine(_folder, orphan[..2], orphan[2..4], orphan);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, [1, 2, 3]);

        Assert.Equal(0, await setup.Maintenance.RemoveOrphansAsync(default));

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - setup.Options.PendingUploadExpiry - TimeSpan.FromHours(1));
        Assert.Equal(1, await setup.Maintenance.RemoveOrphansAsync(default));
        Assert.False(File.Exists(path));
    }
}

/// <summary>
/// The bytes a v2 attestation signs, against a vector computed independently of this code — the same one
/// the client's <c>message_payload_test.dart</c> checks, so the two sides cannot drift apart.
/// </summary>
public class AttestationWithFilesTests
{
    public const string Golden =
        "0000002436663163326433652d346135622d346336642d386537662d3030313132323333343435350000000263310000018bcfe568" +
        "000000000566696c6573000000026869000000010000002430663866616435622d643963622d343639662d613136352d37303836" +
        "37373238393530650001020304050607" + "08090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";

    [Fact]
    public void The_v2_canonical_bytes_are_the_v1_fields_then_each_file_s_id_and_hash()
    {
        var bytes = MessageReportService.CanonicalWithFiles(
            Guid.Parse("6f1c2d3e-4a5b-4c6d-8e7f-001122334455"), "c1", 1_700_000_000_000, "files", "hi",
            [new AttestedAttachment(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"), Enumerable.Range(0, 32).Select(i => (byte)i).ToArray())]);

        var context = "raindrop/message-attestation/v2\n"u8.ToArray();
        Assert.Equal(context, bytes[..context.Length]);
        Assert.Equal(Golden, Convert.ToHexStringLower(bytes[context.Length..]));
    }

    [Fact]
    public void A_file_without_a_whole_hash_is_refused() =>
        Assert.Equal("invalid_attestation", Assert.Throws<ApiException>(() => MessageReportService.CanonicalWithFiles(
            Guid.NewGuid(), "c1", 0, "files", "", [new AttestedAttachment(Guid.NewGuid(), new byte[31])])).Code);
}
