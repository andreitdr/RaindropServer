using System.Data.Common;
using RaindropServer.E2ee;

namespace RaindropServer.Data.Relational;

/// <summary>The <see cref="IIdentityVaultStore"/> every SQL driver shares.</summary>
internal sealed class RelationalIdentityVaultStore : RelationalStore, IIdentityVaultStore
{
    public const string Table = "raindrop_identity_vaults";
    private const string Columns = "user_id, lookup_id, sealed_root, vault, updated_at";

    public RelationalIdentityVaultStore(RelationalDatabaseDriver driver, string connectionString)
        : base(driver, connectionString)
    {
    }

    public Task<IdentityVaultRecord?> FindAsync(Guid userId, CancellationToken cancellationToken) =>
        FindOneAsync($"SELECT {Columns} FROM {Table} WHERE user_id = @user_id",
            command => AddValue(command, "@user_id", userId.ToString()), Read, cancellationToken);

    public Task<IdentityVaultRecord?> FindByLookupAsync(string lookupId, CancellationToken cancellationToken) =>
        FindOneAsync($"SELECT {Columns} FROM {Table} WHERE lookup_id = @lookup_id",
            command => AddValue(command, "@lookup_id", lookupId), Read, cancellationToken);

    public async Task<bool> PutAsync(IdentityVaultRecord vault, CancellationToken cancellationToken)
    {
        void Bind(DbCommand command)
        {
            AddValue(command, "@user_id", vault.UserId.ToString());
            AddValue(command, "@lookup_id", vault.LookupId);
            AddBytes(command, "@sealed_root", vault.SealedRoot);
            AddBytes(command, "@vault", vault.Vault);
            AddTimestamp(command, "@updated_at", vault.UpdatedAtUtc);
        }

        var update =
            $"UPDATE {Table} SET lookup_id = @lookup_id, sealed_root = @sealed_root, vault = @vault, " +
            "updated_at = @updated_at WHERE user_id = @user_id";

        // Update-then-insert rather than a dialect's upsert, to stay inside the SQL all three share.
        // Either statement fails on the unique index when the lookup id is somebody else's.
        try
        {
            if (await ExecuteAsync(update, Bind, cancellationToken) > 0)
                return true;

            await ExecuteAsync(
                $"INSERT INTO {Table} ({Columns}) VALUES (@user_id, @lookup_id, @sealed_root, @vault, @updated_at)",
                Bind, cancellationToken);
            return true;
        }
        catch (DbException)
        {
            // Either another request for this user inserted first, in which case this one's content
            // still wins, or the lookup id is taken, in which case the update fails again.
            try
            {
                return await ExecuteAsync(update, Bind, cancellationToken) > 0;
            }
            catch (DbException)
            {
                return false;
            }
        }
    }

    public Task DeleteAsync(Guid userId, CancellationToken cancellationToken) =>
        ExecuteAsync($"DELETE FROM {Table} WHERE user_id = @user_id",
            command => AddValue(command, "@user_id", userId.ToString()), cancellationToken);

    private IdentityVaultRecord Read(DbDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetFieldValue<byte[]>(2),
        reader.GetFieldValue<byte[]>(3),
        Driver.ReadTimestamp(reader, 4));
}

/// <summary>The <see cref="IAuthenticatorStore"/> every SQL driver shares.</summary>
internal sealed class RelationalAuthenticatorStore : RelationalStore, IAuthenticatorStore
{
    public const string Table = "raindrop_authenticators";

    public RelationalAuthenticatorStore(RelationalDatabaseDriver driver, string connectionString)
        : base(driver, connectionString)
    {
    }

    public Task<AuthenticatorRecord?> FindAsync(Guid userId, CancellationToken cancellationToken) =>
        FindOneAsync($"SELECT user_id, secret, backup_codes, last_step, created_at FROM {Table} WHERE user_id = @user_id",
            command => AddValue(command, "@user_id", userId.ToString()),
            reader => new AuthenticatorRecord(
                Guid.Parse(reader.GetString(0)),
                reader.GetFieldValue<byte[]>(1),
                Split(reader.GetString(2)),
                reader.GetInt64(3),
                Driver.ReadTimestamp(reader, 4)),
            cancellationToken);

    public async Task PutAsync(AuthenticatorRecord authenticator, CancellationToken cancellationToken)
    {
        void Bind(DbCommand command)
        {
            AddValue(command, "@user_id", authenticator.UserId.ToString());
            AddBytes(command, "@secret", authenticator.Secret);
            AddValue(command, "@backup_codes", Join(authenticator.BackupCodeHashes));
            AddValue(command, "@last_step", authenticator.LastUsedStep);
            AddTimestamp(command, "@created_at", authenticator.CreatedAtUtc);
        }

        var update =
            $"UPDATE {Table} SET secret = @secret, backup_codes = @backup_codes, last_step = @last_step, " +
            "created_at = @created_at WHERE user_id = @user_id";

        if (await ExecuteAsync(update, Bind, cancellationToken) > 0)
            return;

        try
        {
            await ExecuteAsync(
                $"INSERT INTO {Table} (user_id, secret, backup_codes, last_step, created_at) " +
                "VALUES (@user_id, @secret, @backup_codes, @last_step, @created_at)",
                Bind, cancellationToken);
        }
        catch (DbException)
        {
            // Another request inserted first; this one's content still wins.
            await ExecuteAsync(update, Bind, cancellationToken);
        }
    }

    public Task DeleteAsync(Guid userId, CancellationToken cancellationToken) =>
        ExecuteAsync($"DELETE FROM {Table} WHERE user_id = @user_id",
            command => AddValue(command, "@user_id", userId.ToString()), cancellationToken);

    public async Task<bool> TryAdvanceStepAsync(Guid userId, long step, CancellationToken cancellationToken) =>
        await ExecuteAsync(
            $"UPDATE {Table} SET last_step = @step WHERE user_id = @user_id AND last_step < @step",
            command =>
            {
                AddValue(command, "@user_id", userId.ToString());
                AddValue(command, "@step", step);
            },
            cancellationToken) > 0;

    public async Task<bool> TryReplaceBackupCodesAsync(
        Guid userId, IReadOnlyList<string> expected, IReadOnlyList<string> remaining, CancellationToken cancellationToken) =>
        await ExecuteAsync(
            $"UPDATE {Table} SET backup_codes = @remaining WHERE user_id = @user_id AND backup_codes = @expected",
            command =>
            {
                AddValue(command, "@user_id", userId.ToString());
                AddValue(command, "@expected", Join(expected));
                AddValue(command, "@remaining", Join(remaining));
            },
            cancellationToken) > 0;

    // Hashes are hex, so a comma can never appear inside one.
    private static string Join(IReadOnlyList<string> hashes) => string.Join(',', hashes);

    private static IReadOnlyList<string> Split(string hashes) =>
        hashes.Split(',', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>The <see cref="IRecoveryStore"/> every SQL driver shares.</summary>
internal sealed class RelationalRecoveryStore : RelationalStore, IRecoveryStore
{
    public const string Table = "raindrop_pending_recoveries";
    private const string Columns = "user_id, device_id, signature_key, requested_at, eligible_at, cancelled_at";

    public RelationalRecoveryStore(RelationalDatabaseDriver driver, string connectionString)
        : base(driver, connectionString)
    {
    }

    public Task<IReadOnlyList<PendingRecoveryRecord>> ListAsync(Guid userId, CancellationToken cancellationToken) =>
        ReadManyAsync($"SELECT {Columns} FROM {Table} WHERE user_id = @user_id ORDER BY requested_at, device_id",
            command => AddValue(command, "@user_id", userId.ToString()), Read, cancellationToken);

    public Task<PendingRecoveryRecord?> FindAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken) =>
        FindOneAsync($"SELECT {Columns} FROM {Table} WHERE user_id = @user_id AND device_id = @device_id",
            command =>
            {
                AddValue(command, "@user_id", userId.ToString());
                AddValue(command, "@device_id", deviceId.ToString());
            },
            Read, cancellationToken);

    public async Task<bool> TryAddAsync(PendingRecoveryRecord recovery, CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAsync(
                $"INSERT INTO {Table} ({Columns}) " +
                "VALUES (@user_id, @device_id, @signature_key, @requested_at, @eligible_at, @cancelled_at)",
                command =>
                {
                    AddValue(command, "@user_id", recovery.UserId.ToString());
                    AddValue(command, "@device_id", recovery.DeviceId.ToString());
                    AddBytes(command, "@signature_key", recovery.SignatureKey);
                    AddTimestamp(command, "@requested_at", recovery.RequestedAtUtc);
                    AddTimestamp(command, "@eligible_at", recovery.EligibleAtUtc);
                    AddTimestamp(command, "@cancelled_at", recovery.CancelledAtUtc);
                },
                cancellationToken);
            return true;
        }
        catch (DbException)
        {
            // The primary key: this device is already waiting.
            return false;
        }
    }

    public Task<int> CancelAllAsync(Guid userId, DateTime cancelledAtUtc, CancellationToken cancellationToken) =>
        ExecuteAsync(
            $"UPDATE {Table} SET cancelled_at = @cancelled_at WHERE user_id = @user_id AND cancelled_at IS NULL",
            command =>
            {
                AddValue(command, "@user_id", userId.ToString());
                AddTimestamp(command, "@cancelled_at", cancelledAtUtc);
            },
            cancellationToken);

    public async Task<bool> DeleteAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken) =>
        await ExecuteAsync($"DELETE FROM {Table} WHERE user_id = @user_id AND device_id = @device_id",
            command =>
            {
                AddValue(command, "@user_id", userId.ToString());
                AddValue(command, "@device_id", deviceId.ToString());
            },
            cancellationToken) > 0;

    public Task DeleteAllAsync(Guid userId, CancellationToken cancellationToken) =>
        ExecuteAsync($"DELETE FROM {Table} WHERE user_id = @user_id",
            command => AddValue(command, "@user_id", userId.ToString()), cancellationToken);

    private PendingRecoveryRecord Read(DbDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        reader.GetFieldValue<byte[]>(2),
        Driver.ReadTimestamp(reader, 3),
        Driver.ReadTimestamp(reader, 4),
        reader.IsDBNull(5) ? null : Driver.ReadTimestamp(reader, 5));
}
