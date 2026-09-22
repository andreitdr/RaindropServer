using RaindropServer.Channels.Text;
using RaindropServer.E2ee;

namespace RaindropServer.Tests;

/// <summary>
/// The delivery service's and the message store's persistence, on every database under test. The
/// concurrency here is real: parallel requests race on one database, as they do in production.
/// </summary>
public class StoreTests
{
    private static HandshakeRecord Commit(Guid channelId, long epoch, byte fill = 1) =>
        new(channelId, epoch, [fill, 2, 3], Guid.NewGuid(), DateTime.UtcNow);

    [Theory]
    [MemberData(nameof(TestDatabases.Providers), MemberType = typeof(TestDatabases))]
    public async Task Racing_commits_for_one_epoch_accept_exactly_one(string provider)
    {
        var (driver, options) = await TestDatabases.OpenAsync(provider);
        var groups = driver.CreateMlsGroupStore(options);
        var channelId = Guid.NewGuid();
        Assert.True(await groups.TryCreateAsync(new MlsGroupRecord(channelId, 0, [9], 0, DateTime.UtcNow), default));
        Assert.False(await groups.TryCreateAsync(new MlsGroupRecord(channelId, 0, [9], 0, DateTime.UtcNow), default));

        var results = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(i => Task.Run(() => groups.TryAppendCommitAsync(Commit(channelId, 0, (byte)i), default))));

        Assert.Equal(1, results.Count(accepted => accepted));
        Assert.Equal(1, (await groups.FindAsync(channelId, default))!.Epoch);

        // The epoch that was taken stays taken; the next one is open.
        Assert.False(await groups.TryAppendCommitAsync(Commit(channelId, 0), default));
        Assert.True(await groups.TryAppendCommitAsync(Commit(channelId, 1), default));
        Assert.False(await groups.TryAppendCommitAsync(Commit(channelId, 5), default));
        Assert.Equal(2, (await groups.FindAsync(channelId, default))!.Epoch);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Providers), MemberType = typeof(TestDatabases))]
    public async Task Group_info_is_kept_only_for_the_current_epoch_and_the_log_pages_and_prunes(string provider)
    {
        var (driver, options) = await TestDatabases.OpenAsync(provider);
        var groups = driver.CreateMlsGroupStore(options);
        var channelId = Guid.NewGuid();
        await groups.TryCreateAsync(new MlsGroupRecord(channelId, 0, null, 0, DateTime.UtcNow), default);
        for (var epoch = 0; epoch < 5; epoch++)
            Assert.True(await groups.TryAppendCommitAsync(Commit(channelId, epoch), default));

        Assert.False(await groups.PutGroupInfoAsync(channelId, 4, [1], DateTime.UtcNow, default));
        Assert.True(await groups.PutGroupInfoAsync(channelId, 5, [7, 7], DateTime.UtcNow, default));
        var group = (await groups.FindAsync(channelId, default))!;
        Assert.Equal([7, 7], group.GroupInfo);
        Assert.Equal(5, group.GroupInfoEpoch);

        Assert.Equal([2L, 3L], (await groups.ReadLogAsync(channelId, 2, 2, default)).Select(c => c.Epoch));
        await groups.PruneLogAsync(channelId, 3, default);
        Assert.Equal(3, await groups.OldestLogEpochAsync(channelId, default));

        await groups.DeleteAsync(channelId, default);
        Assert.Null(await groups.FindAsync(channelId, default));
        Assert.Null(await groups.OldestLogEpochAsync(channelId, default));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Providers), MemberType = typeof(TestDatabases))]
    public async Task Racing_claims_never_hand_out_a_one_time_key_package_twice(string provider)
    {
        var (driver, options) = await TestDatabases.OpenAsync(provider);
        var packages = driver.CreateKeyPackageStore(options);
        var (userId, deviceId) = (Guid.NewGuid(), Guid.NewGuid());
        var now = DateTime.UtcNow;

        await packages.AddAsync(Enumerable.Range(0, 5)
            .Select(i => new KeyPackageRecord(Guid.NewGuid(), userId, deviceId, [(byte)i], false, now.AddSeconds(i)))
            .ToList(), default);
        await packages.ReplaceLastResortAsync(new KeyPackageRecord(Guid.NewGuid(), userId, deviceId, [99], true, now), default);
        Assert.Equal(5, await packages.CountOneTimeAsync(userId, deviceId, default));

        var claims = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => Task.Run(() => packages.ClaimAsync(userId, deviceId, default))));

        var oneTime = claims.Where(c => c is { LastResort: false }).Select(c => c!.PackageId).ToList();
        Assert.Equal(oneTime.Count, oneTime.Distinct().Count());
        Assert.All(claims, c => Assert.NotNull(c)); // the last resort covers the rest
        Assert.Equal(0, await packages.CountOneTimeAsync(userId, deviceId, default));

        // The last resort is never consumed, and a new one replaces the old.
        Assert.Equal([99], (await packages.ClaimAsync(userId, deviceId, default))!.KeyPackage);
        await packages.ReplaceLastResortAsync(new KeyPackageRecord(Guid.NewGuid(), userId, deviceId, [100], true, now), default);
        Assert.Equal([100], (await packages.ClaimAsync(userId, deviceId, default))!.KeyPackage);

        await packages.DeleteForDeviceAsync(userId, deviceId, default);
        Assert.Null(await packages.ClaimAsync(userId, deviceId, default));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Providers), MemberType = typeof(TestDatabases))]
    public async Task Welcomes_wait_for_their_device(string provider)
    {
        var (driver, options) = await TestDatabases.OpenAsync(provider);
        var welcomes = driver.CreateWelcomeStore(options);
        var (userId, deviceId, other) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var first = new WelcomeRecord(Guid.NewGuid(), userId, deviceId, Guid.NewGuid(), 1, [1], DateTime.UtcNow);
        var second = first with { WelcomeId = Guid.NewGuid(), Welcome = [2] };
        await welcomes.AddAsync([first, second, first with { WelcomeId = Guid.NewGuid(), DeviceId = other }], default);

        Assert.Equal(2, (await welcomes.ListAsync(userId, deviceId, default)).Count);
        await welcomes.DeleteAsync(userId, deviceId, [first.WelcomeId], default);
        Assert.Equal([2], (await welcomes.ListAsync(userId, deviceId, default)).Single().Welcome);
        await welcomes.DeleteForDeviceAsync(userId, deviceId, default);
        Assert.Empty(await welcomes.ListAsync(userId, deviceId, default));
        Assert.Single(await welcomes.ListAsync(userId, other, default));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Providers), MemberType = typeof(TestDatabases))]
    public async Task History_pages_exactly_across_plaintext_and_encrypted_messages(string provider)
    {
        var (driver, options) = await TestDatabases.OpenAsync(provider);
        var messages = driver.CreateTextMessageStore(options);
        var channelId = Guid.NewGuid();
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Interleaved, with some sharing a timestamp so the id has to break the tie.
        var written = new List<TextMessageRecord>();
        for (var i = 0; i < 11; i++)
        {
            var record = new TextMessageRecord
            {
                MessageId = Guid.NewGuid(),
                ChannelId = channelId,
                AuthorId = Guid.NewGuid(),
                SentAtUtc = start.AddSeconds(i / 2),
                Content = i % 3 == 0 ? $"plain {i}" : null,
                Ciphertext = i % 3 == 0 ? null : [(byte)i],
                Epoch = i % 3 == 0 ? null : i,
                SenderDeviceId = i % 3 == 0 ? null : Guid.NewGuid()
            };
            await messages.InsertAsync(record, default);
            written.Add(record);
        }

        var seen = new List<TextMessageRecord>();
        MessageCursor? before = null;
        while (true)
        {
            var page = await messages.ReadHistoryAsync(channelId, before, 3, default);
            if (page.Count == 0) break;
            seen.AddRange(page);
            before = MessageCursor.Of(page[^1]);
        }

        var expected = written
            .OrderByDescending(m => m.SentAtUtc)
            .ThenByDescending(m => m.MessageId.ToString(), StringComparer.Ordinal)
            .Select(m => m.MessageId);
        Assert.Equal(expected, seen.Select(m => m.MessageId));

        var encrypted = seen.First(m => m.IsEncrypted);
        Assert.NotNull(encrypted.Ciphertext);
        Assert.Null(encrypted.Content);

        // Deleting an encrypted message nulls its ciphertext and keeps the row.
        var found = (await messages.FindByIdAsync(encrypted.MessageId, default))!;
        await messages.UpdateAsync(found.AsDeleted(Guid.NewGuid(), DateTime.UtcNow), default);
        var tombstone = (await messages.FindByIdAsync(encrypted.MessageId, default))!;
        Assert.True(tombstone.IsDeleted && tombstone.IsEncrypted);
        Assert.Null(tombstone.Ciphertext);

        await messages.DeleteChannelMessagesAsync(channelId, default);
        Assert.Empty(await messages.ReadHistoryAsync(channelId, null, 50, default));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Providers), MemberType = typeof(TestDatabases))]
    public async Task The_first_identity_key_wins_and_revoked_devices_stay_revoked(string provider)
    {
        var (driver, options) = await TestDatabases.OpenAsync(provider);
        var devices = driver.CreateDeviceStore(options);
        var userId = Guid.NewGuid();

        var created = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(() =>
            devices.TryCreateIdentityAsync(new UserIdentityRecord(userId, [(byte)i], DateTime.UtcNow), default))));
        Assert.Equal(1, created.Count(c => c));

        var device = new DeviceRecord(userId, Guid.NewGuid(), new byte[32], [1], DateTime.UtcNow, null);
        Assert.True(await devices.AddDeviceAsync(device, default));
        Assert.False(await devices.AddDeviceAsync(device, default));
        Assert.True(await devices.RevokeDeviceAsync(userId, device.DeviceId, DateTime.UtcNow, default));
        Assert.False(await devices.RevokeDeviceAsync(userId, device.DeviceId, DateTime.UtcNow, default));
        Assert.Empty(await devices.ListDevicesAsync([userId], includeRevoked: false, default));
        Assert.False((await devices.FindDeviceAsync(userId, device.DeviceId, default))!.IsActive);

        await devices.DeleteIdentityAsync(userId, default);
        Assert.Null(await devices.FindIdentityAsync(userId, default));
    }
}
