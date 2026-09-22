using System.Data.Common;
using RaindropServer.Channels.Text;

namespace RaindropServer.Data.Relational;

/// <summary>
/// The one <see cref="ITextMessageStore"/> every SQL driver shares. Written to the same rules as the
/// other relational stores, with one exception: reading history needs a row limit, and that is the
/// single place the three dialects genuinely disagree — so it is asked of the driver rather than
/// written here (see <see cref="RelationalDatabaseDriver.ApplyRowLimit"/>).
/// </summary>
internal sealed class RelationalTextMessageStore : RelationalStore, ITextMessageStore
{
    /// <summary>The table name, shared by the DDL in each driver.</summary>
    public const string Table = "raindrop_text_messages";

    private const string Columns = "message_id, channel_id, author_id, content, sent_at, deleted_at, deleted_by";

    private const string SelectSql = $"SELECT {Columns} FROM {Table}";

    private const string InsertSql =
        $"INSERT INTO {Table} ({Columns}) VALUES " +
        "(@message_id, @channel_id, @author_id, @content, @sent_at, @deleted_at, @deleted_by)";

    /// <summary>
    /// Only the deletion columns change after a message is written. Its author, its channel and the
    /// moment it was sent are what a message is, and none of them is editable.
    /// </summary>
    private const string UpdateSql =
        $"UPDATE {Table} SET content = @content, deleted_at = @deleted_at, deleted_by = @deleted_by " +
        "WHERE message_id = @message_id";

    /// <summary>
    /// Newest first, which is the direction a client pages in. The id breaks ties on identical
    /// timestamps, so a page boundary always falls between two specific rows.
    /// </summary>
    private const string HistoryOrder = "ORDER BY sent_at DESC, message_id DESC";

    /// <summary>
    /// "Strictly older than the cursor": either an earlier timestamp, or the same timestamp and a
    /// lower id. Spelled out rather than using a row-value comparison, which SQL Server lacks.
    /// </summary>
    private const string BeforeCursor =
        "AND (sent_at < @before_at OR (sent_at = @before_at AND message_id < @before_id))";

    // ── End-to-end encrypted messages ────────────────────────────────────────
    //
    // Kept in a table of their own rather than in new columns of the one above: there is no migration
    // mechanism, and a new table is the one change every existing database takes without an ALTER.
    // This store routes each record by IsEncrypted and merges both tables when reading history, so the
    // service and the paging cursor see one timeline.

    /// <summary>The encrypted messages' table, shared by the DDL in each driver.</summary>
    public const string EncryptedTable = "raindrop_encrypted_messages";

    private const string EncryptedColumns =
        "message_id, channel_id, author_id, device_id, epoch, ciphertext, sent_at, deleted_at, deleted_by";

    private const string EncryptedSelectSql = $"SELECT {EncryptedColumns} FROM {EncryptedTable}";

    public RelationalTextMessageStore(RelationalDatabaseDriver driver, string connectionString)
        : base(driver, connectionString)
    {
    }

    public async Task<TextMessageRecord?> FindByIdAsync(Guid messageId, CancellationToken cancellationToken) =>
        await FindOneAsync($"{SelectSql} WHERE message_id = @message_id",
            command => AddValue(command, "@message_id", messageId.ToString()),
            Read,
            cancellationToken)
        ?? await FindOneAsync($"{EncryptedSelectSql} WHERE message_id = @message_id",
            command => AddValue(command, "@message_id", messageId.ToString()),
            ReadEncrypted,
            cancellationToken);

    public Task InsertAsync(TextMessageRecord message, CancellationToken cancellationToken) =>
        message.IsEncrypted ? InsertEncryptedAsync(message, cancellationToken) : InsertPlaintextAsync(message, cancellationToken);

    private Task InsertEncryptedAsync(TextMessageRecord message, CancellationToken cancellationToken) =>
        ExecuteAsync(
            $"""
             INSERT INTO {EncryptedTable} ({EncryptedColumns})
             VALUES (@message_id, @channel_id, @author_id, @device_id, @epoch, @ciphertext, @sent_at, @deleted_at, @deleted_by)
             """,
            command =>
            {
                AddValue(command, "@message_id", message.MessageId.ToString());
                AddValue(command, "@channel_id", message.ChannelId.ToString());
                AddValue(command, "@author_id", message.AuthorId.ToString());
                AddValue(command, "@device_id", message.SenderDeviceId?.ToString());
                AddValue(command, "@epoch", message.Epoch);
                AddValue(command, "@ciphertext", message.Ciphertext);
                AddTimestamp(command, "@sent_at", message.SentAtUtc);
                AddTimestamp(command, "@deleted_at", message.DeletedAtUtc);
                AddValue(command, "@deleted_by", message.DeletedBy?.ToString());
            },
            cancellationToken);

    private Task InsertPlaintextAsync(TextMessageRecord message, CancellationToken cancellationToken) =>
        ExecuteAsync(InsertSql, command =>
        {
            AddValue(command, "@message_id", message.MessageId.ToString());
            AddValue(command, "@channel_id", message.ChannelId.ToString());
            AddValue(command, "@author_id", message.AuthorId.ToString());
            AddValue(command, "@content", message.Content);
            AddTimestamp(command, "@sent_at", message.SentAtUtc);
            AddTimestamp(command, "@deleted_at", message.DeletedAtUtc);
            AddValue(command, "@deleted_by", message.DeletedBy?.ToString());
        }, cancellationToken);

    public async Task UpdateAsync(TextMessageRecord message, CancellationToken cancellationToken)
    {
        var affected = message.IsEncrypted
            ? await ExecuteAsync(
                $"UPDATE {EncryptedTable} SET ciphertext = @ciphertext, deleted_at = @deleted_at, deleted_by = @deleted_by WHERE message_id = @message_id",
                command =>
                {
                    AddValue(command, "@message_id", message.MessageId.ToString());
                    AddValue(command, "@ciphertext", message.Ciphertext);
                    AddTimestamp(command, "@deleted_at", message.DeletedAtUtc);
                    AddValue(command, "@deleted_by", message.DeletedBy?.ToString());
                },
                cancellationToken)
            : await ExecuteAsync(UpdateSql, command =>
        {
            AddValue(command, "@message_id", message.MessageId.ToString());
            AddValue(command, "@content", message.Content);
            AddTimestamp(command, "@deleted_at", message.DeletedAtUtc);
            AddValue(command, "@deleted_by", message.DeletedBy?.ToString());
        }, cancellationToken);

        if (affected == 0)
            throw new InvalidOperationException($"No message row exists for id {message.MessageId}.");
    }

    public async Task<IReadOnlyList<TextMessageRecord>> ReadHistoryAsync(
        Guid channelId, MessageCursor? before, int limit, CancellationToken cancellationToken)
    {
        // A page from each table, then merged: whichever rows are newest overall. Both are ordered
        // the same way, by the same cursor, so paging stays exact across the two.
        var plaintext = await ReadPageAsync(SelectSql, Read, channelId, before, limit, cancellationToken);
        var encrypted = await ReadPageAsync(EncryptedSelectSql, ReadEncrypted, channelId, before, limit, cancellationToken);

        return plaintext.Concat(encrypted)
            .OrderByDescending(m => m.SentAtUtc)
            .ThenByDescending(m => m.MessageId.ToString(), StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    private Task<IReadOnlyList<TextMessageRecord>> ReadPageAsync(
        string selectSql, Func<DbDataReader, TextMessageRecord> read,
        Guid channelId, MessageCursor? before, int limit, CancellationToken cancellationToken)
    {
        var sql = before is null
            ? $"{selectSql} WHERE channel_id = @channel_id {HistoryOrder}"
            : $"{selectSql} WHERE channel_id = @channel_id {BeforeCursor} {HistoryOrder}";

        return ReadManyAsync(Driver.ApplyRowLimit(sql, "@limit"), command =>
        {
            AddValue(command, "@channel_id", channelId.ToString());

            if (before is { } cursor)
            {
                AddTimestamp(command, "@before_at", cursor.SentAtUtc);
                AddValue(command, "@before_id", cursor.MessageId.ToString());
            }

            AddValue(command, "@limit", limit);
        }, read, cancellationToken);
    }

    public async Task DeleteChannelMessagesAsync(Guid channelId, CancellationToken cancellationToken)
    {
        foreach (var table in new[] { Table, EncryptedTable })
        {
            await ExecuteAsync($"DELETE FROM {table} WHERE channel_id = @channel_id",
                command => AddValue(command, "@channel_id", channelId.ToString()),
                cancellationToken);
        }
    }

    public Task<int> PurgePlaintextAsync(CancellationToken cancellationToken) =>
        ExecuteAsync($"DELETE FROM {Table}", _ => { }, cancellationToken);

    private TextMessageRecord ReadEncrypted(DbDataReader reader) => new()
    {
        MessageId = Guid.Parse(reader.GetString(0)),
        ChannelId = Guid.Parse(reader.GetString(1)),
        AuthorId = Guid.Parse(reader.GetString(2)),
        SenderDeviceId = reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
        Epoch = reader.GetInt64(4),
        Ciphertext = reader.IsDBNull(5) ? null : reader.GetFieldValue<byte[]>(5),
        SentAtUtc = Driver.ReadTimestamp(reader, 6),
        DeletedAtUtc = reader.IsDBNull(7) ? null : Driver.ReadTimestamp(reader, 7),
        DeletedBy = reader.IsDBNull(8) ? null : Guid.Parse(reader.GetString(8))
    };

    private TextMessageRecord Read(DbDataReader reader) => new()
    {
        MessageId = Guid.Parse(reader.GetString(0)),
        ChannelId = Guid.Parse(reader.GetString(1)),
        AuthorId = Guid.Parse(reader.GetString(2)),
        Content = reader.IsDBNull(3) ? null : reader.GetString(3),
        SentAtUtc = Driver.ReadTimestamp(reader, 4),
        DeletedAtUtc = reader.IsDBNull(5) ? null : Driver.ReadTimestamp(reader, 5),
        DeletedBy = reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6))
    };
}
