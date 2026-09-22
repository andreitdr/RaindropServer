using RaindropServer.Channels;
using RaindropServer.Channels.Text;
using RaindropServer.Channels.Voice;
using RaindropServer.E2ee;
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
    /// Creates the persistence for users' public identity keys and their devices — what end-to-end
    /// encryption needs the server to remember. Drivers derived from
    /// <see cref="Relational.RelationalDatabaseDriver"/> get it for free; the default refuses, so a
    /// driver written before end-to-end encryption still loads and fails only when this is used.
    /// </summary>
    IDeviceStore CreateDeviceStore(DatabaseOptions options) =>
        throw new NotSupportedException($"The '{Name}' database driver does not support end-to-end encryption devices.");

    /// <summary>Creates the persistence for users' sealed key backups. Same default as <see cref="CreateDeviceStore"/>.</summary>
    IKeyBackupStore CreateKeyBackupStore(DatabaseOptions options) =>
        throw new NotSupportedException($"The '{Name}' database driver does not support key backups.");

    /// <summary>
    /// Creates the MLS delivery service's persistence for groups and their commit logs. Same default
    /// as <see cref="CreateDeviceStore"/>.
    /// </summary>
    IMlsGroupStore CreateMlsGroupStore(DatabaseOptions options) =>
        throw new NotSupportedException($"The '{Name}' database driver does not support end-to-end encrypted groups.");

    /// <summary>Creates the persistence for devices' MLS key packages. Same default as <see cref="CreateDeviceStore"/>.</summary>
    IKeyPackageStore CreateKeyPackageStore(DatabaseOptions options) =>
        throw new NotSupportedException($"The '{Name}' database driver does not support MLS key packages.");

    /// <summary>Creates the persistence for pending MLS Welcomes. Same default as <see cref="CreateDeviceStore"/>.</summary>
    IWelcomeStore CreateWelcomeStore(DatabaseOptions options) =>
        throw new NotSupportedException($"The '{Name}' database driver does not support MLS Welcomes.");

    /// <summary>Creates the persistence for reported messages. Same default as <see cref="CreateDeviceStore"/>.</summary>
    IMessageReportStore CreateMessageReportStore(DatabaseOptions options) =>
        throw new NotSupportedException($"The '{Name}' database driver does not support message reports.");

    /// <summary>
    /// Creates any missing tables and indexes. Must be idempotent: it runs on every startup unless
    /// <see cref="DatabaseOptions.AutoCreateSchema"/> is turned off.
    /// </summary>
    Task EnsureSchemaAsync(DatabaseOptions options, CancellationToken cancellationToken);
}
