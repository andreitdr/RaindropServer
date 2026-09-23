using System.Data.Common;
using RaindropServer.Attachments;

namespace RaindropServer.Data.Relational;

/// <summary>
/// The <see cref="IAttachmentStore"/> every SQL driver shares. Rows describe encrypted files — who
/// uploaded them, for which channel, their declared extension and size, and which message they went
/// out in; the bytes themselves are in an <see cref="IAttachmentStorage"/>.
/// </summary>
internal sealed class RelationalAttachmentStore : RelationalStore, IAttachmentStore
{
    public const string Table = "raindrop_attachments";

    private const string Columns =
        "attachment_id, channel_id, uploader_id, uploader_device_id, kind, extension, ciphertext_size, " +
        "received_size, ciphertext_sha256, state, message_id, created_at, completed_at, deleted_at";

    private static readonly string Uploading = AttachmentNames.Of(AttachmentState.Uploading);
    private static readonly string Uploaded = AttachmentNames.Of(AttachmentState.Uploaded);
    private static readonly string Attached = AttachmentNames.Of(AttachmentState.Attached);
    private static readonly string Deleted = AttachmentNames.Of(AttachmentState.Deleted);

    public RelationalAttachmentStore(RelationalDatabaseDriver driver, string connectionString)
        : base(driver, connectionString)
    {
    }

    public Task InsertAsync(AttachmentRecord attachment, CancellationToken cancellationToken) =>
        ExecuteAsync(
            $"""
             INSERT INTO {Table} ({Columns})
             VALUES (@attachment_id, @channel_id, @uploader_id, @uploader_device_id, @kind, @extension, @ciphertext_size,
                     @received_size, @ciphertext_sha256, @state, @message_id, @created_at, @completed_at, @deleted_at)
             """,
            command =>
            {
                AddValue(command, "@attachment_id", attachment.AttachmentId.ToString());
                AddValue(command, "@channel_id", attachment.ChannelId.ToString());
                AddValue(command, "@uploader_id", attachment.UploaderId.ToString());
                AddValue(command, "@uploader_device_id", attachment.UploaderDeviceId.ToString());
                AddValue(command, "@kind", attachment.Kind);
                AddValue(command, "@extension", attachment.Extension);
                AddValue(command, "@ciphertext_size", attachment.CiphertextSize);
                AddValue(command, "@received_size", attachment.ReceivedSize);
                AddBytes(command, "@ciphertext_sha256", attachment.CiphertextSha256);
                AddValue(command, "@state", AttachmentNames.Of(attachment.State));
                AddValue(command, "@message_id", attachment.MessageId?.ToString());
                AddTimestamp(command, "@created_at", attachment.CreatedAtUtc);
                AddTimestamp(command, "@completed_at", attachment.CompletedAtUtc);
                AddTimestamp(command, "@deleted_at", attachment.DeletedAtUtc);
            },
            cancellationToken);

    public Task<AttachmentRecord?> FindAsync(Guid attachmentId, CancellationToken cancellationToken) =>
        FindOneAsync(
            $"SELECT {Columns} FROM {Table} WHERE attachment_id = @attachment_id",
            command => AddValue(command, "@attachment_id", attachmentId.ToString()),
            Read,
            cancellationToken);

    public async Task<bool> UpdateReceivedAsync(
        Guid attachmentId, long expectedReceivedSize, long receivedSize, CancellationToken cancellationToken) =>
        await ExecuteAsync(
            $"""
             UPDATE {Table} SET received_size = @received_size
             WHERE attachment_id = @attachment_id AND state = @uploading AND received_size = @expected
             """,
            command =>
            {
                AddValue(command, "@received_size", receivedSize);
                AddValue(command, "@attachment_id", attachmentId.ToString());
                AddValue(command, "@uploading", Uploading);
                AddValue(command, "@expected", expectedReceivedSize);
            },
            cancellationToken) == 1;

    public async Task<bool> CompleteAsync(
        Guid attachmentId, byte[] ciphertextSha256, DateTime completedAtUtc, CancellationToken cancellationToken) =>
        await ExecuteAsync(
            $"""
             UPDATE {Table} SET state = @uploaded, ciphertext_sha256 = @ciphertext_sha256, completed_at = @completed_at
             WHERE attachment_id = @attachment_id AND state = @uploading
             """,
            command =>
            {
                AddValue(command, "@uploaded", Uploaded);
                AddBytes(command, "@ciphertext_sha256", ciphertextSha256);
                AddTimestamp(command, "@completed_at", completedAtUtc);
                AddValue(command, "@attachment_id", attachmentId.ToString());
                AddValue(command, "@uploading", Uploading);
            },
            cancellationToken) == 1;

    public async Task<bool> ClaimAsync(
        IReadOnlyList<Guid> attachmentIds, Guid messageId, Guid uploaderId, Guid channelId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await InTransactionAsync(async (connection, transaction) =>
            {
                // One conditional update per file: each either moves from uploaded to attached or
                // touches nothing, so two messages racing for a file cannot both have it. One miss
                // rolls every claim back.
                foreach (var attachmentId in attachmentIds)
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText =
                        $"""
                         UPDATE {Table} SET state = @attached, message_id = @message_id
                         WHERE attachment_id = @attachment_id AND state = @uploaded
                           AND uploader_id = @uploader_id AND channel_id = @channel_id AND message_id IS NULL
                         """;
                    AddValue(command, "@attached", Attached);
                    AddValue(command, "@message_id", messageId.ToString());
                    AddValue(command, "@attachment_id", attachmentId.ToString());
                    AddValue(command, "@uploaded", Uploaded);
                    AddValue(command, "@uploader_id", uploaderId.ToString());
                    AddValue(command, "@channel_id", channelId.ToString());

                    if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                        throw new ClaimRefused();
                }

                return true;
            }, cancellationToken);
        }
        catch (ClaimRefused)
        {
            return false;
        }
    }

    public Task ReleaseAsync(Guid messageId, CancellationToken cancellationToken) =>
        ExecuteAsync(
            $"UPDATE {Table} SET state = @uploaded, message_id = NULL WHERE message_id = @message_id AND state = @attached",
            command =>
            {
                AddValue(command, "@uploaded", Uploaded);
                AddValue(command, "@message_id", messageId.ToString());
                AddValue(command, "@attached", Attached);
            },
            cancellationToken);

    public async Task<IReadOnlyList<AttachmentRecord>> ListForMessagesAsync(
        IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken)
    {
        if (messageIds.Count == 0)
            return [];

        var ids = messageIds.Distinct().ToList();
        var parameters = string.Join(", ", ids.Select((_, index) => $"@m{index}"));

        // Ordered by when each file finished uploading, which is the order the sender uploaded them;
        // a message lists its files in its own order inside the encryption anyway.
        return await ReadManyAsync(
            $"""
             SELECT {Columns} FROM {Table}
             WHERE state = @attached AND message_id IN ({parameters})
             ORDER BY created_at, attachment_id
             """,
            command =>
            {
                AddValue(command, "@attached", Attached);
                for (var index = 0; index < ids.Count; index++)
                    AddValue(command, $"@m{index}", ids[index].ToString());
            },
            Read,
            cancellationToken);
    }

    public async Task<int> CountPendingAsync(Guid uploaderId, CancellationToken cancellationToken) =>
        Convert.ToInt32(await ScalarAsync(
            $"SELECT COUNT(*) FROM {Table} WHERE uploader_id = @uploader_id AND state IN (@uploading, @uploaded)",
            command =>
            {
                AddValue(command, "@uploader_id", uploaderId.ToString());
                AddValue(command, "@uploading", Uploading);
                AddValue(command, "@uploaded", Uploaded);
            },
            cancellationToken));

    public Task MarkDeletedAsync(Guid attachmentId, DateTime deletedAtUtc, CancellationToken cancellationToken) =>
        MarkDeletedWhereAsync("attachment_id = @key", attachmentId, deletedAtUtc, cancellationToken);

    public Task MarkMessageDeletedAsync(Guid messageId, DateTime deletedAtUtc, CancellationToken cancellationToken) =>
        MarkDeletedWhereAsync("message_id = @key", messageId, deletedAtUtc, cancellationToken);

    public Task MarkChannelDeletedAsync(Guid channelId, DateTime deletedAtUtc, CancellationToken cancellationToken) =>
        MarkDeletedWhereAsync("channel_id = @key", channelId, deletedAtUtc, cancellationToken);

    public Task<int> ExpirePendingAsync(DateTime createdBeforeUtc, DateTime deletedAtUtc, CancellationToken cancellationToken) =>
        ExecuteAsync(
            $"""
             UPDATE {Table} SET state = @deleted, deleted_at = @deleted_at
             WHERE state IN (@uploading, @uploaded) AND created_at < @cutoff
             """,
            command =>
            {
                AddValue(command, "@deleted", Deleted);
                AddTimestamp(command, "@deleted_at", deletedAtUtc);
                AddValue(command, "@uploading", Uploading);
                AddValue(command, "@uploaded", Uploaded);
                AddTimestamp(command, "@cutoff", createdBeforeUtc);
            },
            cancellationToken);

    public Task<IReadOnlyList<AttachmentRecord>> ListDeletedAsync(int limit, CancellationToken cancellationToken) =>
        ReadManyAsync(
            Driver.ApplyRowLimit($"SELECT {Columns} FROM {Table} WHERE state = @deleted ORDER BY deleted_at, attachment_id", "@limit"),
            command =>
            {
                AddValue(command, "@deleted", Deleted);
                AddValue(command, "@limit", limit);
            },
            Read,
            cancellationToken);

    public Task RemoveAsync(Guid attachmentId, CancellationToken cancellationToken) =>
        ExecuteAsync(
            $"DELETE FROM {Table} WHERE attachment_id = @attachment_id",
            command => AddValue(command, "@attachment_id", attachmentId.ToString()),
            cancellationToken);

    public async Task<AttachmentStatistics> GetStatisticsAsync(CancellationToken cancellationToken)
    {
        var rows = await ReadManyAsync(
            $"SELECT state, COUNT(*), SUM(ciphertext_size) FROM {Table} GROUP BY state",
            _ => { },
            reader => (
                State: AttachmentNames.ParseState(reader.GetString(0)),
                Count: Convert.ToInt64(reader.GetValue(1)),
                Bytes: reader.IsDBNull(2) ? 0L : Convert.ToInt64(reader.GetValue(2))),
            cancellationToken);

        long Count(params AttachmentState[] states) => rows.Where(row => states.Contains(row.State)).Sum(row => row.Count);
        long Bytes(params AttachmentState[] states) => rows.Where(row => states.Contains(row.State)).Sum(row => row.Bytes);

        return new AttachmentStatistics(
            Count(AttachmentState.Attached),
            Bytes(AttachmentState.Attached),
            Count(AttachmentState.Uploading, AttachmentState.Uploaded),
            Bytes(AttachmentState.Uploading, AttachmentState.Uploaded),
            Count(AttachmentState.Deleted));
    }

    private Task MarkDeletedWhereAsync(string condition, Guid key, DateTime deletedAtUtc, CancellationToken cancellationToken) =>
        ExecuteAsync(
            $"UPDATE {Table} SET state = @deleted, deleted_at = @deleted_at WHERE {condition} AND state <> @deleted",
            command =>
            {
                AddValue(command, "@deleted", Deleted);
                AddTimestamp(command, "@deleted_at", deletedAtUtc);
                AddValue(command, "@key", key.ToString());
            },
            cancellationToken);

    private async Task<object?> ScalarAsync(string sql, Action<DbCommand> bind, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = sql;
        bind(command);

        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private AttachmentRecord Read(DbDataReader reader) => new()
    {
        AttachmentId = Guid.Parse(reader.GetString(0)),
        ChannelId = Guid.Parse(reader.GetString(1)),
        UploaderId = Guid.Parse(reader.GetString(2)),
        UploaderDeviceId = Guid.Parse(reader.GetString(3)),
        Kind = reader.GetString(4),
        Extension = reader.GetString(5),
        CiphertextSize = Convert.ToInt64(reader.GetValue(6)),
        ReceivedSize = Convert.ToInt64(reader.GetValue(7)),
        CiphertextSha256 = reader.IsDBNull(8) ? null : (byte[])reader.GetValue(8),
        State = AttachmentNames.ParseState(reader.GetString(9)),
        MessageId = reader.IsDBNull(10) ? null : Guid.Parse(reader.GetString(10)),
        CreatedAtUtc = Driver.ReadTimestamp(reader, 11),
        CompletedAtUtc = reader.IsDBNull(12) ? null : Driver.ReadTimestamp(reader, 12),
        DeletedAtUtc = reader.IsDBNull(13) ? null : Driver.ReadTimestamp(reader, 13)
    };

    /// <summary>Thrown inside the claim's transaction to roll it back; never leaves this class.</summary>
    private sealed class ClaimRefused : Exception;
}
