using System.Data.Common;
using RaindropServer.E2ee;

namespace RaindropServer.Data.Relational;

/// <summary>
/// The <see cref="IDeviceStore"/> and <see cref="IKeyBackupStore"/> every SQL driver shares, written to
/// the same rules as <see cref="RelationalUserStore"/>: named <c>@parameters</c>, no dialect functions,
/// UUIDs as text. Keys, certificates and backups are opaque byte columns.
/// </summary>
internal sealed class RelationalDeviceStore : RelationalStore, IDeviceStore
{
    public const string IdentityTable = "raindrop_user_identities";
    public const string DeviceTable = "raindrop_devices";

    public RelationalDeviceStore(RelationalDatabaseDriver driver, string connectionString)
        : base(driver, connectionString)
    {
    }

    public Task<UserIdentityRecord?> FindIdentityAsync(Guid userId, CancellationToken cancellationToken) =>
        FindOneAsync($"SELECT user_id, identity_key, updated_at FROM {IdentityTable} WHERE user_id = @user_id",
            command => AddValue(command, "@user_id", userId.ToString()),
            ReadIdentity,
            cancellationToken);

    public async Task<IReadOnlyList<UserIdentityRecord>> FindIdentitiesAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
            return [];

        var names = InList(userIds.Count, "u");
        return await ReadManyAsync(
            $"SELECT user_id, identity_key, updated_at FROM {IdentityTable} WHERE user_id IN ({names})",
            command => BindIds(command, userIds, "u"),
            ReadIdentity,
            cancellationToken);
    }

    public async Task<bool> TryCreateIdentityAsync(UserIdentityRecord identity, CancellationToken cancellationToken)
    {
        if (await FindIdentityAsync(identity.UserId, cancellationToken) is not null)
            return false;

        try
        {
            await ExecuteAsync(
                $"INSERT INTO {IdentityTable} (user_id, identity_key, updated_at) VALUES (@user_id, @key, @updated_at)",
                command => BindIdentity(command, identity),
                cancellationToken);
            return true;
        }
        catch (DbException)
        {
            // Two first devices racing: whichever wrote first holds the identity.
            if (await FindIdentityAsync(identity.UserId, cancellationToken) is not null)
                return false;
            throw;
        }
    }

    public async Task ReplaceIdentityAsync(UserIdentityRecord identity, CancellationToken cancellationToken)
    {
        var updated = await ExecuteAsync(
            $"UPDATE {IdentityTable} SET identity_key = @key, updated_at = @updated_at WHERE user_id = @user_id",
            command => BindIdentity(command, identity),
            cancellationToken);

        if (updated == 0)
            await TryCreateIdentityAsync(identity, cancellationToken);
    }

    public Task DeleteIdentityAsync(Guid userId, CancellationToken cancellationToken) =>
        ExecuteAsync($"DELETE FROM {IdentityTable} WHERE user_id = @user_id",
            command => AddValue(command, "@user_id", userId.ToString()),
            cancellationToken);

    public Task<DeviceRecord?> FindDeviceAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken) =>
        FindOneAsync(
            $"SELECT {DeviceColumns} FROM {DeviceTable} WHERE user_id = @user_id AND device_id = @device_id",
            command =>
            {
                AddValue(command, "@user_id", userId.ToString());
                AddValue(command, "@device_id", deviceId.ToString());
            },
            ReadDevice,
            cancellationToken);

    public async Task<IReadOnlyList<DeviceRecord>> ListDevicesAsync(
        IReadOnlyCollection<Guid> userIds, bool includeRevoked, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
            return [];

        var names = InList(userIds.Count, "u");
        var active = includeRevoked ? "" : " AND revoked_at IS NULL";
        return await ReadManyAsync(
            $"SELECT {DeviceColumns} FROM {DeviceTable} WHERE user_id IN ({names}){active} ORDER BY created_at, device_id",
            command => BindIds(command, userIds, "u"),
            ReadDevice,
            cancellationToken);
    }

    public async Task<bool> AddDeviceAsync(DeviceRecord device, CancellationToken cancellationToken)
    {
        if (await FindDeviceAsync(device.UserId, device.DeviceId, cancellationToken) is not null)
            return false;

        try
        {
            await ExecuteAsync(
                $"""
                 INSERT INTO {DeviceTable} (user_id, device_id, signature_key, certificate, created_at, revoked_at)
                 VALUES (@user_id, @device_id, @signature_key, @certificate, @created_at, @revoked_at)
                 """,
                command =>
                {
                    AddValue(command, "@user_id", device.UserId.ToString());
                    AddValue(command, "@device_id", device.DeviceId.ToString());
                    AddBytes(command, "@signature_key", device.SignatureKey);
                    AddBytes(command, "@certificate", device.Certificate);
                    AddTimestamp(command, "@created_at", device.CreatedAtUtc);
                    AddTimestamp(command, "@revoked_at", device.RevokedAtUtc);
                },
                cancellationToken);
            return true;
        }
        catch (DbException)
        {
            if (await FindDeviceAsync(device.UserId, device.DeviceId, cancellationToken) is not null)
                return false;
            throw;
        }
    }

    public async Task<bool> RevokeDeviceAsync(Guid userId, Guid deviceId, DateTime revokedAtUtc, CancellationToken cancellationToken) =>
        await ExecuteAsync(
            $"UPDATE {DeviceTable} SET revoked_at = @revoked_at WHERE user_id = @user_id AND device_id = @device_id AND revoked_at IS NULL",
            command =>
            {
                AddTimestamp(command, "@revoked_at", revokedAtUtc);
                AddValue(command, "@user_id", userId.ToString());
                AddValue(command, "@device_id", deviceId.ToString());
            },
            cancellationToken) > 0;

    public Task<int> RevokeAllDevicesAsync(Guid userId, DateTime revokedAtUtc, CancellationToken cancellationToken) =>
        ExecuteAsync(
            $"UPDATE {DeviceTable} SET revoked_at = @revoked_at WHERE user_id = @user_id AND revoked_at IS NULL",
            command =>
            {
                AddTimestamp(command, "@revoked_at", revokedAtUtc);
                AddValue(command, "@user_id", userId.ToString());
            },
            cancellationToken);

    private const string DeviceColumns = "user_id, device_id, signature_key, certificate, created_at, revoked_at";

    private DeviceRecord ReadDevice(DbDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        reader.GetFieldValue<byte[]>(2),
        reader.GetFieldValue<byte[]>(3),
        Driver.ReadTimestamp(reader, 4),
        reader.IsDBNull(5) ? null : Driver.ReadTimestamp(reader, 5));

    private UserIdentityRecord ReadIdentity(DbDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetFieldValue<byte[]>(1),
        Driver.ReadTimestamp(reader, 2));

    private void BindIdentity(DbCommand command, UserIdentityRecord identity)
    {
        AddValue(command, "@user_id", identity.UserId.ToString());
        AddBytes(command, "@key", identity.IdentityKey);
        AddTimestamp(command, "@updated_at", identity.UpdatedAtUtc);
    }

    private static string InList(int count, string prefix) =>
        string.Join(", ", Enumerable.Range(0, count).Select(i => $"@{prefix}{i}"));

    private static void BindIds(DbCommand command, IReadOnlyCollection<Guid> ids, string prefix)
    {
        var i = 0;
        foreach (var id in ids)
            AddValue(command, $"@{prefix}{i++}", id.ToString());
    }
}

internal sealed class RelationalKeyBackupStore : RelationalStore, IKeyBackupStore
{
    public const string Table = "raindrop_key_backups";

    public RelationalKeyBackupStore(RelationalDatabaseDriver driver, string connectionString)
        : base(driver, connectionString)
    {
    }

    public Task<KeyBackupRecord?> FindAsync(Guid userId, CancellationToken cancellationToken) =>
        FindOneAsync($"SELECT user_id, blob_data, updated_at FROM {Table} WHERE user_id = @user_id",
            command => AddValue(command, "@user_id", userId.ToString()),
            reader => new KeyBackupRecord(
                Guid.Parse(reader.GetString(0)),
                reader.GetFieldValue<byte[]>(1),
                Driver.ReadTimestamp(reader, 2)),
            cancellationToken);

    public async Task PutAsync(KeyBackupRecord backup, CancellationToken cancellationToken)
    {
        void Bind(DbCommand command)
        {
            AddValue(command, "@user_id", backup.UserId.ToString());
            AddBytes(command, "@blob", backup.Blob);
            AddTimestamp(command, "@updated_at", backup.UpdatedAtUtc);
        }

        // Update-then-insert rather than a dialect's upsert, to stay inside the SQL all three share.
        var updated = await ExecuteAsync(
            $"UPDATE {Table} SET blob_data = @blob, updated_at = @updated_at WHERE user_id = @user_id",
            Bind, cancellationToken);

        if (updated > 0)
            return;

        try
        {
            await ExecuteAsync(
                $"INSERT INTO {Table} (user_id, blob_data, updated_at) VALUES (@user_id, @blob, @updated_at)",
                Bind, cancellationToken);
        }
        catch (DbException)
        {
            // Another request inserted first; this one's content still wins.
            await ExecuteAsync(
                $"UPDATE {Table} SET blob_data = @blob, updated_at = @updated_at WHERE user_id = @user_id",
                Bind, cancellationToken);
        }
    }

    public Task DeleteAsync(Guid userId, CancellationToken cancellationToken) =>
        ExecuteAsync($"DELETE FROM {Table} WHERE user_id = @user_id",
            command => AddValue(command, "@user_id", userId.ToString()),
            cancellationToken);
}
