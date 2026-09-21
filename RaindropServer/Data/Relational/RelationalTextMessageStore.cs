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

    public RelationalTextMessageStore(RelationalDatabaseDriver driver, string connectionString)
        : base(driver, connectionString)
    {
    }

    public Task<TextMessageRecord?> FindByIdAsync(Guid messageId, CancellationToken cancellationToken) =>
        FindOneAsync($"{SelectSql} WHERE message_id = @message_id",
            command => AddValue(command, "@message_id", messageId.ToString()),
            Read,
            cancellationToken);

    public Task InsertAsync(TextMessageRecord message, CancellationToken cancellationToken) =>
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
        var affected = await ExecuteAsync(UpdateSql, command =>
        {
            AddValue(command, "@message_id", message.MessageId.ToString());
            AddValue(command, "@content", message.Content);
            AddTimestamp(command, "@deleted_at", message.DeletedAtUtc);
            AddValue(command, "@deleted_by", message.DeletedBy?.ToString());
        }, cancellationToken);

        if (affected == 0)
            throw new InvalidOperationException($"No message row exists for id {message.MessageId}.");
    }

    public Task<IReadOnlyList<TextMessageRecord>> ReadHistoryAsync(
        Guid channelId, MessageCursor? before, int limit, CancellationToken cancellationToken)
    {
        var sql = before is null
            ? $"{SelectSql} WHERE channel_id = @channel_id {HistoryOrder}"
            : $"{SelectSql} WHERE channel_id = @channel_id {BeforeCursor} {HistoryOrder}";

        return ReadManyAsync(Driver.ApplyRowLimit(sql, "@limit"), command =>
        {
            AddValue(command, "@channel_id", channelId.ToString());

            if (before is { } cursor)
            {
                AddTimestamp(command, "@before_at", cursor.SentAtUtc);
                AddValue(command, "@before_id", cursor.MessageId.ToString());
            }

            AddValue(command, "@limit", limit);
        }, Read, cancellationToken);
    }

    public Task DeleteChannelMessagesAsync(Guid channelId, CancellationToken cancellationToken) =>
        ExecuteAsync($"DELETE FROM {Table} WHERE channel_id = @channel_id",
            command => AddValue(command, "@channel_id", channelId.ToString()),
            cancellationToken);

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
