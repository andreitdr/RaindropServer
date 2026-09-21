using RaindropServer.Channels;
using RaindropServer.Channels.Text;
using RaindropServer.Channels.Voice;
using RaindropServer.Roles;
using RaindropServer.Users;

namespace RaindropServer.Data;

/// <summary>
/// A database module. Implement this in its own assembly to teach Raindrop about another database;
/// the server discovers drivers automatically (see <see cref="DatabaseDriverRegistry"/>) and uses
/// whichever one the server owner enabled by name.
/// </summary>
/// <remarks>
/// Most drivers will not implement this directly — deriving from
/// <see cref="Relational.RelationalDatabaseDriver"/> leaves only a connection factory and the
/// schema DDL to write.
/// </remarks>
public interface IDatabaseDriver
{
    /// <summary>
    /// The name a server owner puts in their configuration to enable this driver, e.g. "postgres".
    /// Matched case-insensitively and required to be unique across installed drivers.
    /// </summary>
    string Name { get; }

    /// <summary>Creates the user persistence backed by this database.</summary>
    IUserStore CreateUserStore(DatabaseOptions options);

    /// <summary>Creates the channel persistence backed by this database.</summary>
    IChannelStore CreateChannelStore(DatabaseOptions options);

    /// <summary>Creates the persistence for roles and for which members hold them.</summary>
    IRoleStore CreateRoleStore(DatabaseOptions options);

    /// <summary>
    /// Creates the persistence for the messages in text channels. One factory per kind of channel
    /// content: a voice call itself is never stored, only the moderation that outlives it — see
    /// <see cref="CreateVoiceMuteStore"/>.
    /// </summary>
    ITextMessageStore CreateTextMessageStore(DatabaseOptions options);

    /// <summary>
    /// Creates the persistence for the original text of deleted messages — the copy kept for the
    /// server owner when a message or a whole channel is deleted.
    /// </summary>
    IDeletedMessageStore CreateDeletedMessageStore(DatabaseOptions options);

    /// <summary>
    /// Creates the persistence for members muted in every voice call by a moderator — the one piece
    /// of voice state that has to survive a restart.
    /// </summary>
    IVoiceMuteStore CreateVoiceMuteStore(DatabaseOptions options);

    /// <summary>
    /// Creates any missing tables and indexes. Must be idempotent: it runs on every startup unless
    /// <see cref="DatabaseOptions.AutoCreateSchema"/> is turned off.
    /// </summary>
    Task EnsureSchemaAsync(DatabaseOptions options, CancellationToken cancellationToken);
}
