using System.Data.Common;
using RaindropServer.E2ee;

namespace RaindropServer.Data.Relational;

/// <summary>
/// The MLS delivery service's persistence, shared by every SQL driver: groups and their commit logs,
/// key packages, and pending Welcomes. Every message is an opaque byte column. The commit log's
/// primary key is <c>(channel_id, epoch)</c>, so the database itself refuses a second commit for an
/// epoch: that is what keeps a group from forking, whatever races the requests run.
/// </summary>
internal sealed class RelationalMlsGroupStore : RelationalStore, IMlsGroupStore
{
    public const string GroupTable = "raindrop_mls_groups";
    public const string CommitTable = "raindrop_mls_commits";
    public const string CommitDeviceTable = "raindrop_mls_commit_devices";
    public const string ResetTable = "raindrop_mls_group_resets";
    public const string BanTable = "raindrop_mls_group_bans";

    /// <summary>The generation of the group in <c>g</c>: its channel's latest reset, or 0.</summary>
    private const string GenerationOfGroup =
        $"COALESCE((SELECT MAX(r.generation) FROM {ResetTable} r WHERE r.channel_id = g.channel_id), 0)";

    public RelationalMlsGroupStore(RelationalDatabaseDriver driver, string connectionString)
        : base(driver, connectionString)
    {
    }

    public Task<MlsGroupRecord?> FindAsync(Guid channelId, CancellationToken cancellationToken) =>
        FindOneAsync(
            $"""
             SELECT g.channel_id, g.epoch, g.group_info, g.group_info_epoch, g.updated_at, {GenerationOfGroup}
             FROM {GroupTable} g WHERE g.channel_id = @channel_id
             """,
            command => AddValue(command, "@channel_id", channelId.ToString()),
            reader => new MlsGroupRecord(
                Guid.Parse(reader.GetString(0)),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<byte[]>(2),
                reader.GetInt64(3),
                Driver.ReadTimestamp(reader, 4),
                Convert.ToInt64(reader.GetValue(5))),
            cancellationToken);

    public Task<IReadOnlyList<Guid>> ListChannelIdsAsync(CancellationToken cancellationToken) =>
        ReadManyAsync($"SELECT channel_id FROM {GroupTable}", _ => { }, reader => Guid.Parse(reader.GetString(0)),
            cancellationToken);

    public async Task<bool> TryCreateAsync(MlsGroupRecord group, CancellationToken cancellationToken)
    {
        if (await FindAsync(group.ChannelId, cancellationToken) is not null)
            return false;

        try
        {
            await ExecuteAsync(
                $"""
                 INSERT INTO {GroupTable} (channel_id, epoch, group_info, group_info_epoch, updated_at)
                 VALUES (@channel_id, @epoch, @group_info, @group_info_epoch, @updated_at)
                 """,
                command =>
                {
                    AddValue(command, "@channel_id", group.ChannelId.ToString());
                    AddValue(command, "@epoch", group.Epoch);
                    AddBytes(command, "@group_info", group.GroupInfo);
                    AddValue(command, "@group_info_epoch", group.GroupInfoEpoch);
                    AddTimestamp(command, "@updated_at", group.UpdatedAtUtc);
                },
                cancellationToken);
            return true;
        }
        catch (DbException)
        {
            if (await FindAsync(group.ChannelId, cancellationToken) is not null)
                return false;
            throw;
        }
    }

    public async Task<bool> TryAppendCommitAsync(HandshakeRecord commit, CancellationToken cancellationToken)
    {
        try
        {
            return await InTransactionAsync(async (connection, transaction) =>
            {
                // Advance the group only from the epoch the commit was made in. Zero rows means
                // another commit already moved it on.
                await using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText =
                        $"UPDATE {GroupTable} SET epoch = @next, updated_at = @at WHERE channel_id = @channel_id AND epoch = @epoch";
                    AddValue(update, "@next", commit.Epoch + 1);
                    AddTimestamp(update, "@at", commit.CreatedAtUtc);
                    AddValue(update, "@channel_id", commit.ChannelId.ToString());
                    AddValue(update, "@epoch", commit.Epoch);
                    if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
                        return false;
                }

                // The primary key backs the check above up: a second commit for this epoch cannot be
                // stored even if the update somehow let it through.
                await using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText =
                        $"""
                         INSERT INTO {CommitTable} (channel_id, epoch, message, sender_user_id, created_at)
                         VALUES (@channel_id, @epoch, @message, @sender, @created_at)
                         """;
                    AddValue(insert, "@channel_id", commit.ChannelId.ToString());
                    AddValue(insert, "@epoch", commit.Epoch);
                    AddBytes(insert, "@message", commit.Message);
                    AddValue(insert, "@sender", commit.SenderUserId.ToString());
                    AddTimestamp(insert, "@created_at", commit.CreatedAtUtc);
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }

                // Which device submitted it, beside the log (a table of its own so the log needs no
                // ALTER), in the same transaction: a commit is never stored unattributed.
                if (commit.SenderDeviceId is { } deviceId)
                {
                    await using var attribute = connection.CreateCommand();
                    attribute.Transaction = transaction;
                    attribute.CommandText =
                        $"INSERT INTO {CommitDeviceTable} (channel_id, epoch, device_id) VALUES (@channel_id, @epoch, @device_id)";
                    AddValue(attribute, "@channel_id", commit.ChannelId.ToString());
                    AddValue(attribute, "@epoch", commit.Epoch);
                    AddValue(attribute, "@device_id", deviceId.ToString());
                    await attribute.ExecuteNonQueryAsync(cancellationToken);
                }

                return true;
            }, cancellationToken);
        }
        catch (DbException)
        {
            // The key refused a duplicate: another commit won this epoch. Anything else is a real
            // failure, and is not dressed up as a lost race.
            if ((await ReadLogAsync(commit.ChannelId, commit.Epoch, 1, cancellationToken)) is [{ } existing]
                && existing.Epoch == commit.Epoch)
                return false;
            throw;
        }
    }

    public async Task<bool> PutGroupInfoAsync(
        Guid channelId, long epoch, byte[] groupInfo, DateTime atUtc, CancellationToken cancellationToken) =>
        await ExecuteAsync(
            $"""
             UPDATE {GroupTable} SET group_info = @group_info, group_info_epoch = @epoch, updated_at = @at
             WHERE channel_id = @channel_id AND epoch = @epoch
             """,
            command =>
            {
                AddBytes(command, "@group_info", groupInfo);
                AddValue(command, "@epoch", epoch);
                AddTimestamp(command, "@at", atUtc);
                AddValue(command, "@channel_id", channelId.ToString());
            },
            cancellationToken) > 0;

    public Task<IReadOnlyList<HandshakeRecord>> ReadLogAsync(
        Guid channelId, long fromEpoch, int limit, CancellationToken cancellationToken) =>
        ReadManyAsync(
            Driver.ApplyRowLimit(
                $"""
                 SELECT c.channel_id, c.epoch, c.message, c.sender_user_id, c.created_at, d.device_id
                 FROM {CommitTable} c
                 LEFT JOIN {CommitDeviceTable} d ON d.channel_id = c.channel_id AND d.epoch = c.epoch
                 WHERE c.channel_id = @channel_id AND c.epoch >= @from ORDER BY c.epoch
                 """,
                "@limit"),
            command =>
            {
                AddValue(command, "@channel_id", channelId.ToString());
                AddValue(command, "@from", fromEpoch);
                AddValue(command, "@limit", limit);
            },
            reader => new HandshakeRecord(
                Guid.Parse(reader.GetString(0)),
                reader.GetInt64(1),
                reader.GetFieldValue<byte[]>(2),
                Guid.Parse(reader.GetString(3)),
                Driver.ReadTimestamp(reader, 4),
                reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5))),
            cancellationToken);

    public async Task<long?> OldestLogEpochAsync(Guid channelId, CancellationToken cancellationToken)
    {
        var rows = await ReadManyAsync(
            $"SELECT MIN(epoch) FROM {CommitTable} WHERE channel_id = @channel_id",
            command => AddValue(command, "@channel_id", channelId.ToString()),
            reader => reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0),
            cancellationToken);
        return rows.FirstOrDefault();
    }

    public async Task PruneLogAsync(Guid channelId, long epoch, CancellationToken cancellationToken)
    {
        foreach (var table in new[] { CommitTable, CommitDeviceTable })
        {
            await ExecuteAsync($"DELETE FROM {table} WHERE channel_id = @channel_id AND epoch < @epoch",
                command =>
                {
                    AddValue(command, "@channel_id", channelId.ToString());
                    AddValue(command, "@epoch", epoch);
                },
                cancellationToken);
        }
    }

    public async Task DeleteAsync(Guid channelId, CancellationToken cancellationToken)
    {
        foreach (var table in new[]
                 {
                     CommitTable, CommitDeviceTable, RelationalWelcomeStore.Table, ResetTable, BanTable, GroupTable
                 })
        {
            await ExecuteAsync($"DELETE FROM {table} WHERE channel_id = @channel_id",
                command => AddValue(command, "@channel_id", channelId.ToString()),
                cancellationToken);
        }
    }

    // ── Generations ──────────────────────────────────────────────────────────

    public async Task<long> GenerationAsync(Guid channelId, CancellationToken cancellationToken)
    {
        var rows = await ReadManyAsync(
            $"SELECT MAX(generation) FROM {ResetTable} WHERE channel_id = @channel_id",
            command => AddValue(command, "@channel_id", channelId.ToString()),
            reader => reader.IsDBNull(0) ? 0L : Convert.ToInt64(reader.GetValue(0)),
            cancellationToken);
        return rows.FirstOrDefault();
    }

    public async Task<bool> TryResetAsync(MlsGroupResetRecord reset, CancellationToken cancellationToken)
    {
        try
        {
            return await InTransactionAsync(async (connection, transaction) =>
            {
                // The reset row first: its key refuses a second reset to the same generation, and
                // then nothing below runs.
                await using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText =
                        $"""
                         INSERT INTO {ResetTable} (channel_id, generation, reset_at, requested_by_user_id,
                             requested_by_device_id, reported_kind, reported_epoch, reported_sha256,
                             sender_user_id, sender_device_id)
                         VALUES (@channel_id, @generation, @reset_at, @user_id, @device_id, @kind, @epoch,
                             @sha256, @sender_user_id, @sender_device_id)
                         """;
                    AddValue(insert, "@channel_id", reset.ChannelId.ToString());
                    AddValue(insert, "@generation", reset.Generation);
                    AddTimestamp(insert, "@reset_at", reset.ResetAtUtc);
                    AddValue(insert, "@user_id", reset.RequestedByUserId.ToString());
                    AddValue(insert, "@device_id", reset.RequestedByDeviceId.ToString());
                    AddValue(insert, "@kind", reset.ReportedKind);
                    AddValue(insert, "@epoch", reset.ReportedEpoch);
                    AddBytes(insert, "@sha256", reset.ReportedSha256);
                    AddValue(insert, "@sender_user_id", reset.SenderUserId?.ToString());
                    AddValue(insert, "@sender_device_id", reset.SenderDeviceId?.ToString());
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }

                // The previous generation goes: its group, its log (the new one starts again at
                // epoch 0, under the same key) and the Welcomes into it.
                foreach (var table in new[] { CommitTable, CommitDeviceTable, RelationalWelcomeStore.Table, GroupTable })
                {
                    await using var delete = connection.CreateCommand();
                    delete.Transaction = transaction;
                    delete.CommandText = $"DELETE FROM {table} WHERE channel_id = @channel_id";
                    AddValue(delete, "@channel_id", reset.ChannelId.ToString());
                    await delete.ExecuteNonQueryAsync(cancellationToken);
                }

                return true;
            }, cancellationToken);
        }
        catch (DbException)
        {
            if (await GenerationAsync(reset.ChannelId, cancellationToken) >= reset.Generation)
                return false;
            throw;
        }
    }

    private const string ResetColumns =
        "channel_id, generation, reset_at, requested_by_user_id, requested_by_device_id, reported_kind, " +
        "reported_epoch, reported_sha256, sender_user_id, sender_device_id";

    public Task<IReadOnlyList<MlsGroupResetRecord>> ListResetsAsync(
        Guid channelId, int limit, CancellationToken cancellationToken) =>
        ReadManyAsync(
            Driver.ApplyRowLimit(
                $"SELECT {ResetColumns} FROM {ResetTable} WHERE channel_id = @channel_id ORDER BY generation DESC",
                "@limit"),
            command =>
            {
                AddValue(command, "@channel_id", channelId.ToString());
                AddValue(command, "@limit", limit);
            },
            ReadReset,
            cancellationToken);

    public Task<MlsGroupResetRecord?> LatestResetByAsync(Guid channelId, Guid userId, CancellationToken cancellationToken) =>
        FindOneAsync(
            Driver.ApplyRowLimit(
                $"""
                 SELECT {ResetColumns} FROM {ResetTable}
                 WHERE channel_id = @channel_id AND requested_by_user_id = @user_id ORDER BY generation DESC
                 """,
                "@limit"),
            command =>
            {
                AddValue(command, "@channel_id", channelId.ToString());
                AddValue(command, "@user_id", userId.ToString());
                AddValue(command, "@limit", 1);
            },
            ReadReset,
            cancellationToken);

    private MlsGroupResetRecord ReadReset(DbDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetInt64(1),
        Driver.ReadTimestamp(reader, 2),
        Guid.Parse(reader.GetString(3)),
        Guid.Parse(reader.GetString(4)),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetInt64(6),
        reader.IsDBNull(7) ? null : reader.GetFieldValue<byte[]>(7),
        reader.IsDBNull(8) ? null : Guid.Parse(reader.GetString(8)),
        reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9)));

    // ── Bans ─────────────────────────────────────────────────────────────────

    // A ban on every device of an account is stored with the empty UUID as its device, because a
    // primary-key column cannot be null on SQL Server or PostgreSQL.
    private static string DeviceKey(Guid? deviceId) => (deviceId ?? Guid.Empty).ToString();

    public async Task<bool> TryBanAsync(MlsGroupBanRecord ban, CancellationToken cancellationToken)
    {
        if (await HasBanAsync(ban.ChannelId, ban.UserId, ban.DeviceId, cancellationToken))
            return false;

        try
        {
            await ExecuteAsync(
                $"""
                 INSERT INTO {BanTable} (channel_id, user_id, device_id, banned_by, banned_at)
                 VALUES (@channel_id, @user_id, @device_id, @banned_by, @banned_at)
                 """,
                command =>
                {
                    AddValue(command, "@channel_id", ban.ChannelId.ToString());
                    AddValue(command, "@user_id", ban.UserId.ToString());
                    AddValue(command, "@device_id", DeviceKey(ban.DeviceId));
                    AddValue(command, "@banned_by", ban.BannedBy.ToString());
                    AddTimestamp(command, "@banned_at", ban.BannedAtUtc);
                },
                cancellationToken);
            return true;
        }
        catch (DbException)
        {
            if (await HasBanAsync(ban.ChannelId, ban.UserId, ban.DeviceId, cancellationToken))
                return false;
            throw;
        }
    }

    public async Task<bool> UnbanAsync(Guid channelId, Guid userId, Guid? deviceId, CancellationToken cancellationToken) =>
        await ExecuteAsync(
            $"DELETE FROM {BanTable} WHERE channel_id = @channel_id AND user_id = @user_id AND device_id = @device_id",
            command =>
            {
                AddValue(command, "@channel_id", channelId.ToString());
                AddValue(command, "@user_id", userId.ToString());
                AddValue(command, "@device_id", DeviceKey(deviceId));
            },
            cancellationToken) > 0;

    public Task<IReadOnlyList<MlsGroupBanRecord>> ListBansAsync(Guid channelId, CancellationToken cancellationToken) =>
        ReadManyAsync(
            $"""
             SELECT channel_id, user_id, device_id, banned_by, banned_at FROM {BanTable}
             WHERE channel_id = @channel_id ORDER BY banned_at, user_id, device_id
             """,
            command => AddValue(command, "@channel_id", channelId.ToString()),
            reader =>
            {
                var device = Guid.Parse(reader.GetString(2));
                return new MlsGroupBanRecord(
                    Guid.Parse(reader.GetString(0)),
                    Guid.Parse(reader.GetString(1)),
                    device == Guid.Empty ? null : device,
                    Guid.Parse(reader.GetString(3)),
                    Driver.ReadTimestamp(reader, 4));
            },
            cancellationToken);

    public async Task<bool> IsBannedAsync(Guid channelId, Guid userId, Guid? deviceId, CancellationToken cancellationToken)
    {
        var rows = await ReadManyAsync(
            $"""
             SELECT COUNT(*) FROM {BanTable}
             WHERE channel_id = @channel_id AND user_id = @user_id AND device_id IN (@every, @device)
             """,
            command =>
            {
                AddValue(command, "@channel_id", channelId.ToString());
                AddValue(command, "@user_id", userId.ToString());
                AddValue(command, "@every", DeviceKey(null));
                AddValue(command, "@device", DeviceKey(deviceId));
            },
            reader => Convert.ToInt64(reader.GetValue(0)),
            cancellationToken);
        return rows.FirstOrDefault() > 0;
    }

    private async Task<bool> HasBanAsync(Guid channelId, Guid userId, Guid? deviceId, CancellationToken cancellationToken)
    {
        var rows = await ReadManyAsync(
            $"SELECT COUNT(*) FROM {BanTable} WHERE channel_id = @channel_id AND user_id = @user_id AND device_id = @device_id",
            command =>
            {
                AddValue(command, "@channel_id", channelId.ToString());
                AddValue(command, "@user_id", userId.ToString());
                AddValue(command, "@device_id", DeviceKey(deviceId));
            },
            reader => Convert.ToInt64(reader.GetValue(0)),
            cancellationToken);
        return rows.FirstOrDefault() > 0;
    }
}

internal sealed class RelationalKeyPackageStore : RelationalStore, IKeyPackageStore
{
    public const string Table = "raindrop_mls_key_packages";

    public RelationalKeyPackageStore(RelationalDatabaseDriver driver, string connectionString)
        : base(driver, connectionString)
    {
    }

    public async Task AddAsync(IReadOnlyList<KeyPackageRecord> packages, CancellationToken cancellationToken)
    {
        foreach (var package in packages)
        {
            await ExecuteAsync(
                $"""
                 INSERT INTO {Table} (package_id, user_id, device_id, key_package, last_resort, created_at)
                 VALUES (@package_id, @user_id, @device_id, @key_package, @last_resort, @created_at)
                 """,
                command => Bind(command, package),
                cancellationToken);
        }
    }

    public async Task<int> CountOneTimeAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken)
    {
        var rows = await ReadManyAsync(
            $"SELECT COUNT(*) FROM {Table} WHERE user_id = @user_id AND device_id = @device_id AND last_resort = 0",
            command => BindDevice(command, userId, deviceId),
            reader => Convert.ToInt32(reader.GetValue(0)),
            cancellationToken);
        return rows.FirstOrDefault();
    }

    public async Task ReplaceLastResortAsync(KeyPackageRecord package, CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            $"DELETE FROM {Table} WHERE user_id = @user_id AND device_id = @device_id AND last_resort = 1",
            command => BindDevice(command, package.UserId, package.DeviceId),
            cancellationToken);
        await AddAsync([package with { LastResort = true }], cancellationToken);
    }

    public async Task<KeyPackageRecord?> ClaimAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken)
    {
        // Two members adding the same device at once may pick the same row; whoever deletes it has
        // claimed it, and the other tries the next.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var candidate = await FindOneAsync(
                Driver.ApplyRowLimit(
                    $"SELECT {Columns} FROM {Table} WHERE user_id = @user_id AND device_id = @device_id AND last_resort = 0 ORDER BY created_at, package_id",
                    "@limit"),
                command =>
                {
                    BindDevice(command, userId, deviceId);
                    AddValue(command, "@limit", 1);
                },
                Read,
                cancellationToken);

            if (candidate is null)
                break;

            var claimed = await ExecuteAsync($"DELETE FROM {Table} WHERE package_id = @package_id",
                command => AddValue(command, "@package_id", candidate.PackageId.ToString()),
                cancellationToken);
            if (claimed > 0)
                return candidate;
        }

        return await FindOneAsync(
            $"SELECT {Columns} FROM {Table} WHERE user_id = @user_id AND device_id = @device_id AND last_resort = 1",
            command => BindDevice(command, userId, deviceId),
            Read,
            cancellationToken);
    }

    public Task DeleteForDeviceAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken) =>
        ExecuteAsync($"DELETE FROM {Table} WHERE user_id = @user_id AND device_id = @device_id",
            command => BindDevice(command, userId, deviceId),
            cancellationToken);

    private const string Columns = "package_id, user_id, device_id, key_package, last_resort, created_at";

    private KeyPackageRecord Read(DbDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        Guid.Parse(reader.GetString(2)),
        reader.GetFieldValue<byte[]>(3),
        Convert.ToInt32(reader.GetValue(4)) != 0,
        Driver.ReadTimestamp(reader, 5));

    private void Bind(DbCommand command, KeyPackageRecord package)
    {
        AddValue(command, "@package_id", package.PackageId.ToString());
        BindDevice(command, package.UserId, package.DeviceId);
        AddBytes(command, "@key_package", package.KeyPackage);
        AddValue(command, "@last_resort", package.LastResort ? 1 : 0);
        AddTimestamp(command, "@created_at", package.CreatedAtUtc);
    }

    private static void BindDevice(DbCommand command, Guid userId, Guid deviceId)
    {
        AddValue(command, "@user_id", userId.ToString());
        AddValue(command, "@device_id", deviceId.ToString());
    }
}

internal sealed class RelationalWelcomeStore : RelationalStore, IWelcomeStore
{
    public const string Table = "raindrop_mls_welcomes";

    public RelationalWelcomeStore(RelationalDatabaseDriver driver, string connectionString)
        : base(driver, connectionString)
    {
    }

    public async Task AddAsync(IReadOnlyList<WelcomeRecord> welcomes, CancellationToken cancellationToken)
    {
        foreach (var welcome in welcomes)
        {
            await ExecuteAsync(
                $"""
                 INSERT INTO {Table} (welcome_id, user_id, device_id, channel_id, epoch, welcome, created_at)
                 VALUES (@welcome_id, @user_id, @device_id, @channel_id, @epoch, @welcome, @created_at)
                 """,
                command =>
                {
                    AddValue(command, "@welcome_id", welcome.WelcomeId.ToString());
                    AddValue(command, "@user_id", welcome.UserId.ToString());
                    AddValue(command, "@device_id", welcome.DeviceId.ToString());
                    AddValue(command, "@channel_id", welcome.ChannelId.ToString());
                    AddValue(command, "@epoch", welcome.Epoch);
                    AddBytes(command, "@welcome", welcome.Welcome);
                    AddTimestamp(command, "@created_at", welcome.CreatedAtUtc);
                },
                cancellationToken);
        }
    }

    public Task<IReadOnlyList<WelcomeRecord>> ListAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken) =>
        ReadManyAsync(
            $"""
             SELECT welcome_id, user_id, device_id, channel_id, epoch, welcome, created_at FROM {Table}
             WHERE user_id = @user_id AND device_id = @device_id ORDER BY created_at, welcome_id
             """,
            command =>
            {
                AddValue(command, "@user_id", userId.ToString());
                AddValue(command, "@device_id", deviceId.ToString());
            },
            reader => new WelcomeRecord(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)),
                Guid.Parse(reader.GetString(3)),
                reader.GetInt64(4),
                reader.GetFieldValue<byte[]>(5),
                Driver.ReadTimestamp(reader, 6)),
            cancellationToken);

    public async Task DeleteAsync(Guid userId, Guid deviceId, IReadOnlyCollection<Guid> welcomeIds, CancellationToken cancellationToken)
    {
        foreach (var id in welcomeIds)
        {
            await ExecuteAsync(
                $"DELETE FROM {Table} WHERE welcome_id = @welcome_id AND user_id = @user_id AND device_id = @device_id",
                command =>
                {
                    AddValue(command, "@welcome_id", id.ToString());
                    AddValue(command, "@user_id", userId.ToString());
                    AddValue(command, "@device_id", deviceId.ToString());
                },
                cancellationToken);
        }
    }

    public Task DeleteForDeviceAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken) =>
        ExecuteAsync($"DELETE FROM {Table} WHERE user_id = @user_id AND device_id = @device_id",
            command =>
            {
                AddValue(command, "@user_id", userId.ToString());
                AddValue(command, "@device_id", deviceId.ToString());
            },
            cancellationToken);
}
