using System.Data;
using System.Data.Common;
using RaindropServer.Channels;
using RaindropServer.Channels.Text;
using RaindropServer.Channels.Voice;
using RaindropServer.Roles;
using RaindropServer.Users;

namespace RaindropServer.Data.Relational;

/// <summary>
/// The base class real drivers derive from. Every SQL database Raindrop supports runs the same
/// queries — see <see cref="RelationalUserStore"/> — so a driver only has to supply a connection,
/// the DDL for its dialect, and, where it differs, how a timestamp is bound to a parameter.
/// </summary>
public abstract class RelationalDatabaseDriver : IDatabaseDriver
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <summary>Opens a connection object (not yet opened) for this database.</summary>
    protected internal abstract DbConnection CreateConnection(string connectionString);

    /// <summary>
    /// The idempotent DDL for this dialect, one statement per entry. Statements run in order on every
    /// startup, so each must tolerate the objects already existing.
    /// </summary>
    protected internal abstract IReadOnlyList<string> SchemaStatements { get; }

    /// <summary>
    /// Binds a UTC timestamp to a parameter. The default drops the UTC marker, which is what a
    /// timezone-less column type expects; a driver whose column carries a timezone overrides this.
    /// All timestamps in the schema are UTC regardless.
    /// </summary>
    protected internal virtual void BindTimestamp(DbParameter parameter, DateTime utc)
        => parameter.Value = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);

    /// <summary>Reads a timestamp back as UTC, whatever kind the provider hands over.</summary>
    protected internal virtual DateTime ReadTimestamp(DbDataReader reader, int ordinal)
    {
        var value = reader.GetDateTime(ordinal);

        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    /// <summary>
    /// Appends a row limit to a query that already has an <c>ORDER BY</c>. The one piece of SQL the
    /// three engines do not agree on: SQLite and PostgreSQL take <c>LIMIT</c>, SQL Server wants the
    /// standard <c>OFFSET … FETCH</c>, so it overrides this.
    /// </summary>
    /// <param name="orderedSql">The query to limit, ordered — an unordered limit returns arbitrary rows.</param>
    /// <param name="parameterName">The name of the parameter carrying the row count, e.g. "@limit".</param>
    protected internal virtual string ApplyRowLimit(string orderedSql, string parameterName) =>
        $"{orderedSql} LIMIT {parameterName}";

    /// <inheritdoc />
    public IUserStore CreateUserStore(DatabaseOptions options) => new RelationalUserStore(this, options.ConnectionString);

    /// <inheritdoc />
    public IChannelStore CreateChannelStore(DatabaseOptions options) => new RelationalChannelStore(this, options.ConnectionString);

    /// <inheritdoc />
    public IRoleStore CreateRoleStore(DatabaseOptions options) => new RelationalRoleStore(this, options.ConnectionString);

    /// <inheritdoc />
    public ITextMessageStore CreateTextMessageStore(DatabaseOptions options) =>
        new RelationalTextMessageStore(this, options.ConnectionString);

    /// <inheritdoc />
    public IDeletedMessageStore CreateDeletedMessageStore(DatabaseOptions options) =>
        new RelationalDeletedMessageStore(this, options.ConnectionString);

    /// <inheritdoc />
    public IVoiceMuteStore CreateVoiceMuteStore(DatabaseOptions options) =>
        new RelationalVoiceMuteStore(this, options.ConnectionString);

    /// <inheritdoc />
    public async Task EnsureSchemaAsync(DatabaseOptions options, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        foreach (var statement in SchemaStatements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.CommandType = CommandType.Text;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
