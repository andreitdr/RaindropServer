using System.Data.Common;
using RaindropServer.Channels.Text;

namespace RaindropServer.Data.Relational;

/// <summary>
/// The <see cref="IMessageReportStore"/> every SQL driver shares. The one table on the server that
/// holds the text of end-to-end encrypted messages — only what a member chose to reveal by
/// reporting it.
/// </summary>
internal sealed class RelationalMessageReportStore : RelationalStore, IMessageReportStore
{
    public const string Table = "raindrop_message_reports";

    private const string Columns =
        "report_id, message_id, channel_id, author_id, device_id, reporter_id, body, reason, sent_at, reported_at";

    public RelationalMessageReportStore(RelationalDatabaseDriver driver, string connectionString)
        : base(driver, connectionString)
    {
    }

    public Task AddAsync(MessageReportRecord report, CancellationToken cancellationToken) =>
        ExecuteAsync(
            $"""
             INSERT INTO {Table} ({Columns})
             VALUES (@report_id, @message_id, @channel_id, @author_id, @device_id, @reporter_id, @body, @reason, @sent_at, @reported_at)
             """,
            command =>
            {
                AddValue(command, "@report_id", report.ReportId.ToString());
                AddValue(command, "@message_id", report.MessageId.ToString());
                AddValue(command, "@channel_id", report.ChannelId.ToString());
                AddValue(command, "@author_id", report.AuthorId.ToString());
                AddValue(command, "@device_id", report.SenderDeviceId.ToString());
                AddValue(command, "@reporter_id", report.ReporterId.ToString());
                AddValue(command, "@body", report.Body);
                AddValue(command, "@reason", report.Reason);
                AddTimestamp(command, "@sent_at", report.MessageSentAtUtc);
                AddTimestamp(command, "@reported_at", report.ReportedAtUtc);
            },
            cancellationToken);

    public Task<IReadOnlyList<MessageReportRecord>> ListForChannelAsync(Guid channelId, int limit, CancellationToken cancellationToken) =>
        ReadManyAsync(
            Driver.ApplyRowLimit($"SELECT {Columns} FROM {Table} WHERE channel_id = @channel_id ORDER BY reported_at DESC, report_id DESC", "@limit"),
            command =>
            {
                AddValue(command, "@channel_id", channelId.ToString());
                AddValue(command, "@limit", limit);
            },
            Read,
            cancellationToken);

    public Task<IReadOnlyList<MessageReportRecord>> ListRecentAsync(int limit, CancellationToken cancellationToken) =>
        ReadManyAsync(
            Driver.ApplyRowLimit($"SELECT {Columns} FROM {Table} ORDER BY reported_at DESC, report_id DESC", "@limit"),
            command => AddValue(command, "@limit", limit),
            Read,
            cancellationToken);

    private MessageReportRecord Read(DbDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        Guid.Parse(reader.GetString(2)),
        Guid.Parse(reader.GetString(3)),
        Guid.Parse(reader.GetString(4)),
        Guid.Parse(reader.GetString(5)),
        reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        Driver.ReadTimestamp(reader, 8),
        Driver.ReadTimestamp(reader, 9));
}
