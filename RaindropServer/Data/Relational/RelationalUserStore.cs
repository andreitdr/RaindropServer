using System.Data.Common;
using RaindropServer.Users;

namespace RaindropServer.Data.Relational;

/// <summary>
/// The one <see cref="IUserStore"/> every SQL driver shares. The statements below stay inside the
/// common subset of SQLite, PostgreSQL and SQL Server — named <c>@parameters</c>, no dialect
/// functions, and no row limiting, since every lookup hits a primary key or a unique index.
/// </summary>
/// <remarks>
/// A UUID is stored as its 36-character text form rather than a native GUID column: the three
/// databases disagree on how GUIDs are typed and byte-ordered, and text keeps a database portable
/// between them.
/// </remarks>
internal sealed class RelationalUserStore : RelationalStore, IUserStore
{
    /// <summary>The table name, shared by the DDL in each driver.</summary>
    public const string Table = "raindrop_users";

    private const string Columns =
        "user_id, username, username_normalized, password_hash, status, created_at, last_login_at, left_at, data_expires_at";

    private const string SelectSql = $"SELECT {Columns} FROM {Table}";

    private const string InsertSql =
        $"INSERT INTO {Table} ({Columns}) VALUES " +
        "(@user_id, @username, @username_normalized, @password_hash, @status, @created_at, @last_login_at, @left_at, @data_expires_at)";

    private const string UpdateSql =
        $"UPDATE {Table} SET username = @username, username_normalized = @username_normalized, " +
        "password_hash = @password_hash, status = @status, created_at = @created_at, " +
        "last_login_at = @last_login_at, left_at = @left_at, data_expires_at = @data_expires_at " +
        "WHERE user_id = @user_id";

    public RelationalUserStore(RelationalDatabaseDriver driver, string connectionString)
        : base(driver, connectionString)
    {
    }

    public Task<UserRecord?> FindByIdAsync(Guid userId, CancellationToken cancellationToken) =>
        FindOneAsync($"{SelectSql} WHERE user_id = @user_id",
            command => AddValue(command, "@user_id", userId.ToString()),
            Read,
            cancellationToken);

    public Task<UserRecord?> FindByUsernameAsync(string usernameNormalized, CancellationToken cancellationToken) =>
        FindOneAsync($"{SelectSql} WHERE username_normalized = @username_normalized",
            command => AddValue(command, "@username_normalized", usernameNormalized),
            Read,
            cancellationToken);

    public async Task<bool> UsernameExistsAsync(string usernameNormalized, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = $"SELECT COUNT(*) FROM {Table} WHERE username_normalized = @username_normalized";
        AddValue(command, "@username_normalized", usernameNormalized);

        var count = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(count) > 0;
    }

    public async Task InsertAsync(UserRecord user, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = InsertSql;
        BindUser(command, user);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (DbException ex)
        {
            throw await TranslateWriteFailureAsync(ex, user, cancellationToken);
        }
    }

    public async Task UpdateAsync(UserRecord user, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = UpdateSql;
        BindUser(command, user);

        int affected;
        try
        {
            affected = await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (DbException ex)
        {
            throw await TranslateWriteFailureAsync(ex, user, cancellationToken);
        }

        if (affected == 0)
            throw new InvalidOperationException($"No user row exists for UUID {user.UserId}.");
    }

    public Task<IReadOnlyList<UserRecord>> ListByStatusAsync(UserStatus status, CancellationToken cancellationToken) =>
        ReadManyAsync($"{SelectSql} WHERE status = @status ORDER BY username_normalized",
            command => AddValue(command, "@status", (int)status),
            Read,
            cancellationToken);

    public Task<IReadOnlyList<UserRecord>> FindExpiredPreservedAsync(DateTime utcNow, CancellationToken cancellationToken) =>
        ReadManyAsync(
            $"{SelectSql} WHERE status = @status AND data_expires_at IS NOT NULL AND data_expires_at <= @now",
            command =>
            {
                AddValue(command, "@status", (int)UserStatus.LeftDataPreserved);
                AddTimestamp(command, "@now", utcNow);
            },
            Read,
            cancellationToken);

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private UserRecord Read(DbDataReader reader) => new()
    {
        UserId = Guid.Parse(reader.GetString(0)),
        Username = reader.IsDBNull(1) ? null : reader.GetString(1),
        UsernameNormalized = reader.IsDBNull(2) ? null : reader.GetString(2),
        PasswordHash = reader.IsDBNull(3) ? null : reader.GetString(3),
        Status = (UserStatus)reader.GetInt32(4),
        CreatedAtUtc = Driver.ReadTimestamp(reader, 5),
        LastLoginAtUtc = reader.IsDBNull(6) ? null : Driver.ReadTimestamp(reader, 6),
        LeftAtUtc = reader.IsDBNull(7) ? null : Driver.ReadTimestamp(reader, 7),
        DataExpiresAtUtc = reader.IsDBNull(8) ? null : Driver.ReadTimestamp(reader, 8)
    };

    private void BindUser(DbCommand command, UserRecord user)
    {
        AddValue(command, "@user_id", user.UserId.ToString());
        AddValue(command, "@username", user.Username);
        AddValue(command, "@username_normalized", user.UsernameNormalized);
        AddValue(command, "@password_hash", user.PasswordHash);
        AddValue(command, "@status", (int)user.Status);
        AddTimestamp(command, "@created_at", user.CreatedAtUtc);
        AddTimestamp(command, "@last_login_at", user.LastLoginAtUtc);
        AddTimestamp(command, "@left_at", user.LeftAtUtc);
        AddTimestamp(command, "@data_expires_at", user.DataExpiresAtUtc);
    }

    /// <summary>
    /// Turns a constraint violation into the API error it really is. The service checks a username
    /// before claiming it, but two clients can still race between the check and the insert — the
    /// unique index is what actually keeps usernames unique, and this is how that surfaces.
    /// </summary>
    private async Task<Exception> TranslateWriteFailureAsync(
        DbException ex, UserRecord user, CancellationToken cancellationToken)
    {
        if (user.UsernameNormalized is not { } normalized)
            return ex;

        var owner = await FindByUsernameAsync(normalized, cancellationToken);

        if (owner is not null && owner.UserId != user.UserId)
            return ApiException.Conflict("username_taken", "That username is already taken on this server.");

        return ex;
    }
}
