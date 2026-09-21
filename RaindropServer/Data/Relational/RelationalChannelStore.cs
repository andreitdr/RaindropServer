using System.Data.Common;
using RaindropServer.Channels;

namespace RaindropServer.Data.Relational;

/// <summary>
/// The one <see cref="IChannelStore"/> every SQL driver shares, written to the same rules as
/// <see cref="RelationalUserStore"/>: named <c>@parameters</c>, no dialect functions, and UUIDs
/// stored as 36-character text so a database stays portable between the three engines.
/// </summary>
internal sealed class RelationalChannelStore : RelationalStore, IChannelStore
{
    /// <summary>The table name, shared by the DDL in each driver.</summary>
    public const string Table = "raindrop_channels";

    private const string Columns = "channel_id, channel_type, name, name_normalized, created_by, created_at";

    private const string SelectSql = $"SELECT {Columns} FROM {Table}";

    private const string InsertSql =
        $"INSERT INTO {Table} ({Columns}) VALUES " +
        "(@channel_id, @channel_type, @name, @name_normalized, @created_by, @created_at)";

    private const string UpdateSql =
        $"UPDATE {Table} SET channel_type = @channel_type, name = @name, name_normalized = @name_normalized, " +
        "created_by = @created_by, created_at = @created_at WHERE channel_id = @channel_id";

    public RelationalChannelStore(RelationalDatabaseDriver driver, string connectionString)
        : base(driver, connectionString)
    {
    }

    public Task<ChannelRecord?> FindByIdAsync(Guid channelId, CancellationToken cancellationToken) =>
        FindOneAsync($"{SelectSql} WHERE channel_id = @channel_id",
            command => AddValue(command, "@channel_id", channelId.ToString()),
            Read,
            cancellationToken);

    public Task<ChannelRecord?> FindByNameAsync(string nameNormalized, CancellationToken cancellationToken) =>
        FindOneAsync($"{SelectSql} WHERE name_normalized = @name_normalized",
            command => AddValue(command, "@name_normalized", nameNormalized),
            Read,
            cancellationToken);

    public Task<IReadOnlyList<ChannelRecord>> ListAsync(CancellationToken cancellationToken) =>
        ReadManyAsync($"{SelectSql} ORDER BY name_normalized", _ => { }, Read, cancellationToken);

    public async Task InsertAsync(ChannelRecord channel, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = InsertSql;
        Bind(command, channel);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (DbException ex)
        {
            throw await TranslateWriteFailureAsync(ex, channel, cancellationToken);
        }
    }

    public async Task UpdateAsync(ChannelRecord channel, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = UpdateSql;
        Bind(command, channel);

        int affected;
        try
        {
            affected = await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (DbException ex)
        {
            throw await TranslateWriteFailureAsync(ex, channel, cancellationToken);
        }

        if (affected == 0)
            throw new InvalidOperationException($"No channel row exists for id {channel.ChannelId}.");
    }

    public Task DeleteAsync(Guid channelId, CancellationToken cancellationToken) =>
        ExecuteAsync($"DELETE FROM {Table} WHERE channel_id = @channel_id",
            command => AddValue(command, "@channel_id", channelId.ToString()),
            cancellationToken);

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private ChannelRecord Read(DbDataReader reader) => new()
    {
        ChannelId = Guid.Parse(reader.GetString(0)),
        Type = (ChannelType)reader.GetInt32(1),
        Name = reader.GetString(2),
        NameNormalized = reader.GetString(3),
        CreatedBy = Guid.Parse(reader.GetString(4)),
        CreatedAtUtc = Driver.ReadTimestamp(reader, 5)
    };

    private void Bind(DbCommand command, ChannelRecord channel)
    {
        AddValue(command, "@channel_id", channel.ChannelId.ToString());
        AddValue(command, "@channel_type", (int)channel.Type);
        AddValue(command, "@name", channel.Name);
        AddValue(command, "@name_normalized", channel.NameNormalized);
        AddValue(command, "@created_by", channel.CreatedBy.ToString());
        AddTimestamp(command, "@created_at", channel.CreatedAtUtc);
    }

    /// <summary>
    /// Turns the unique-name constraint into the API error it really is. The service checks the name
    /// before claiming it, but two clients can still race between the check and the insert — the
    /// unique index is what actually keeps names unique, and this is how that surfaces.
    /// </summary>
    private async Task<Exception> TranslateWriteFailureAsync(
        DbException ex, ChannelRecord channel, CancellationToken cancellationToken)
    {
        var owner = await FindByNameAsync(channel.NameNormalized, cancellationToken);

        if (owner is not null && owner.ChannelId != channel.ChannelId)
            return ApiException.Conflict("channel_name_taken", "A channel with that name already exists on this server.");

        return ex;
    }
}
