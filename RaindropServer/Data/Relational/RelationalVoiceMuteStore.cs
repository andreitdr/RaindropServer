using System.Data.Common;
using RaindropServer.Channels.Voice;

namespace RaindropServer.Data.Relational;

/// <summary>
/// The one <see cref="IVoiceMuteStore"/> every SQL driver shares, written to the same rules as
/// <see cref="RelationalUserStore"/>: named <c>@parameters</c>, no dialect functions, UUIDs as text.
/// </summary>
internal sealed class RelationalVoiceMuteStore : RelationalStore, IVoiceMuteStore
{
    /// <summary>The table name, shared by the DDL in each driver.</summary>
    public const string Table = "raindrop_voice_mutes";

    public RelationalVoiceMuteStore(RelationalDatabaseDriver driver, string connectionString)
        : base(driver, connectionString)
    {
    }

    public Task<VoiceMuteRecord?> FindAsync(Guid userId, CancellationToken cancellationToken) =>
        FindOneAsync($"SELECT user_id, muted_by, muted_at FROM {Table} WHERE user_id = @user_id",
            command => AddValue(command, "@user_id", userId.ToString()),
            reader => new VoiceMuteRecord(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                Driver.ReadTimestamp(reader, 2)),
            cancellationToken);

    public async Task<bool> AddAsync(VoiceMuteRecord mute, CancellationToken cancellationToken)
    {
        // Asked first so that muting somebody already muted reports "no change" rather than failing on
        // the primary key — the same shape as assigning a role somebody already holds.
        if (await FindAsync(mute.UserId, cancellationToken) is not null)
            return false;

        try
        {
            await ExecuteAsync(
                $"INSERT INTO {Table} (user_id, muted_by, muted_at) VALUES (@user_id, @muted_by, @muted_at)",
                command =>
                {
                    AddValue(command, "@user_id", mute.UserId.ToString());
                    AddValue(command, "@muted_by", mute.MutedBy.ToString());
                    AddTimestamp(command, "@muted_at", mute.MutedAtUtc);
                },
                cancellationToken);

            return true;
        }
        catch (DbException)
        {
            // A moderator racing us muted them first; either way they are muted and we changed nothing.
            if (await FindAsync(mute.UserId, cancellationToken) is not null)
                return false;

            throw;
        }
    }

    public async Task<bool> RemoveAsync(Guid userId, CancellationToken cancellationToken) =>
        await ExecuteAsync($"DELETE FROM {Table} WHERE user_id = @user_id",
            command => AddValue(command, "@user_id", userId.ToString()),
            cancellationToken) > 0;
}
