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

    public RelationalMlsGroupStore(RelationalDatabaseDriver driver, string connectionString)
        : base(driver, connectionString)
    {
    }

    public Task<MlsGroupRecord?> FindAsync(Guid channelId, CancellationToken cancellationToken) =>
        FindOneAsync(
            $"SELECT channel_id, epoch, group_info, group_info_epoch, updated_at FROM {GroupTable} WHERE channel_id = @channel_id",
            command => AddValue(command, "@channel_id", channelId.ToString()),
            reader => new MlsGroupRecord(
                Guid.Parse(reader.GetString(0)),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<byte[]>(2),
                reader.GetInt64(3),
                Driver.ReadTimestamp(reader, 4)),
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
                    AddValue(command, "@group_info", group.GroupInfo);
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
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    $"""
                     INSERT INTO {CommitTable} (channel_id, epoch, message, sender_user_id, created_at)
                     VALUES (@channel_id, @epoch, @message, @sender, @created_at)
                     """;
                AddValue(insert, "@channel_id", commit.ChannelId.ToString());
                AddValue(insert, "@epoch", commit.Epoch);
                AddValue(insert, "@message", commit.Message);
                AddValue(insert, "@sender", commit.SenderUserId.ToString());
                AddTimestamp(insert, "@created_at", commit.CreatedAtUtc);
                await insert.ExecuteNonQueryAsync(cancellationToken);
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
                AddValue(command, "@group_info", groupInfo);
                AddValue(command, "@epoch", epoch);
                AddTimestamp(command, "@at", atUtc);
                AddValue(command, "@channel_id", channelId.ToString());
            },
            cancellationToken) > 0;

    public Task<IReadOnlyList<HandshakeRecord>> ReadLogAsync(
        Guid channelId, long fromEpoch, int limit, CancellationToken cancellationToken) =>
        ReadManyAsync(
            Driver.ApplyRowLimit(
                $"SELECT channel_id, epoch, message, sender_user_id, created_at FROM {CommitTable} WHERE channel_id = @channel_id AND epoch >= @from ORDER BY epoch",
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
                Driver.ReadTimestamp(reader, 4)),
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

    public Task PruneLogAsync(Guid channelId, long epoch, CancellationToken cancellationToken) =>
        ExecuteAsync($"DELETE FROM {CommitTable} WHERE channel_id = @channel_id AND epoch < @epoch",
            command =>
            {
                AddValue(command, "@channel_id", channelId.ToString());
                AddValue(command, "@epoch", epoch);
            },
            cancellationToken);

    public async Task DeleteAsync(Guid channelId, CancellationToken cancellationToken)
    {
        foreach (var table in new[] { CommitTable, RelationalWelcomeStore.Table, GroupTable })
        {
            await ExecuteAsync($"DELETE FROM {table} WHERE channel_id = @channel_id",
                command => AddValue(command, "@channel_id", channelId.ToString()),
                cancellationToken);
        }
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
        AddValue(command, "@key_package", package.KeyPackage);
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
                    AddValue(command, "@welcome", welcome.Welcome);
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
