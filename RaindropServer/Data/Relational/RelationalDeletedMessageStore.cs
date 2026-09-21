using RaindropServer.Channels;
using RaindropServer.Channels.Text;

namespace RaindropServer.Data.Relational;

/// <summary>
/// The one <see cref="IDeletedMessageStore"/> every SQL driver shares. Both statements stay inside
/// the common subset of the three dialects, and both end in <c>WHERE NOT EXISTS</c> so that running
/// one twice — which a retried deletion does — leaves the first copy, and its <c>deleted_by</c>, alone.
/// </summary>
internal sealed class RelationalDeletedMessageStore : RelationalStore, IDeletedMessageStore
{
    /// <summary>The table name, shared by the DDL in each driver.</summary>
    public const string Table = "raindrop_deleted_messages";

    private const string Columns =
        "message_id, channel_id, channel_name, author_id, content, sent_at, deleted_at, deleted_by, reason";

    private const string InsertMessageSql =
        $"INSERT INTO {Table} ({Columns}) " +
        "SELECT @message_id, @channel_id, @channel_name, @author_id, @content, @sent_at, @deleted_at, @deleted_by, @reason " +
        $"WHERE NOT EXISTS (SELECT 1 FROM {Table} WHERE message_id = @message_id)";

    /// <summary>
    /// A whole channel in one statement, so there is no window in which some of it is preserved and
    /// the rest is not, and no need to hold a big channel in memory. Rows with no content are
    /// tombstones and are skipped.
    /// </summary>
    private const string InsertChannelSql =
        $"INSERT INTO {Table} ({Columns}) " +
        "SELECT m.message_id, m.channel_id, @channel_name, m.author_id, m.content, m.sent_at, @deleted_at, @deleted_by, @reason " +
        $"FROM {RelationalTextMessageStore.Table} m " +
        "WHERE m.channel_id = @channel_id AND m.content IS NOT NULL " +
        $"AND NOT EXISTS (SELECT 1 FROM {Table} a WHERE a.message_id = m.message_id)";

    public RelationalDeletedMessageStore(RelationalDatabaseDriver driver, string connectionString)
        : base(driver, connectionString)
    {
    }

    public Task ArchiveMessageAsync(DeletedMessageRecord message, CancellationToken cancellationToken) =>
        ExecuteAsync(InsertMessageSql, command =>
        {
            AddValue(command, "@message_id", message.MessageId.ToString());
            AddValue(command, "@channel_id", message.ChannelId.ToString());
            AddValue(command, "@channel_name", message.ChannelName);
            AddValue(command, "@author_id", message.AuthorId.ToString());
            AddValue(command, "@content", message.Content);
            AddTimestamp(command, "@sent_at", message.SentAtUtc);
            AddTimestamp(command, "@deleted_at", message.DeletedAtUtc);
            AddValue(command, "@deleted_by", message.DeletedBy.ToString());
            AddValue(command, "@reason", message.Reason);
        }, cancellationToken);

    public Task ArchiveChannelMessagesAsync(ChannelDeletion deletion, CancellationToken cancellationToken) =>
        ExecuteAsync(InsertChannelSql, command =>
        {
            AddValue(command, "@channel_id", deletion.Channel.ChannelId.ToString());
            AddValue(command, "@channel_name", deletion.Channel.Name);
            AddTimestamp(command, "@deleted_at", deletion.DeletedAtUtc);
            AddValue(command, "@deleted_by", deletion.DeletedBy.ToString());
            AddValue(command, "@reason", DeletedMessageReasons.ChannelDeleted);
        }, cancellationToken);
}
