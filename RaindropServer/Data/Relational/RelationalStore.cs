using System.Data.Common;

namespace RaindropServer.Data.Relational;

/// <summary>
/// The ADO.NET plumbing every relational store shares: opening a connection from the driver, running
/// a statement, and reading rows back. Stores derive from this and are left holding nothing but
/// their SQL and their mapping.
/// </summary>
/// <remarks>
/// Connections are opened per operation and disposed straight after, which leaves pooling to the
/// provider — the one place that knows how expensive a connection is for that database.
/// </remarks>
internal abstract class RelationalStore
{
    private readonly string _connectionString;

    protected RelationalStore(RelationalDatabaseDriver driver, string connectionString)
    {
        Driver = driver;
        _connectionString = connectionString;
    }

    /// <summary>The driver, consulted for the connection and for how this database handles timestamps.</summary>
    protected RelationalDatabaseDriver Driver { get; }

    /// <summary>Opens a connection, disposing it again if opening throws.</summary>
    protected async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = Driver.CreateConnection(_connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Runs a statement that returns nothing, and reports how many rows it touched.</summary>
    protected async Task<int> ExecuteAsync(string sql, Action<DbCommand> bind, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = sql;
        bind(command);

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Reads the first row of a query, or null when there is none.</summary>
    protected async Task<T?> FindOneAsync<T>(
        string sql, Action<DbCommand> bind, Func<DbDataReader, T> read, CancellationToken cancellationToken)
        where T : class
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = sql;
        bind(command);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? read(reader) : null;
    }

    /// <summary>Reads every row of a query, in the order the query returned them.</summary>
    protected async Task<IReadOnlyList<T>> ReadManyAsync<T>(
        string sql, Action<DbCommand> bind, Func<DbDataReader, T> read, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = sql;
        bind(command);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(read(reader));

        return rows;
    }

    /// <summary>Adds a parameter, mapping a null to <see cref="DBNull"/>.</summary>
    protected static void AddValue(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    /// <summary>Adds a UTC timestamp parameter, bound the way this database expects one.</summary>
    protected void AddTimestamp(DbCommand command, string name, DateTime? utc)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;

        if (utc is null)
            parameter.Value = DBNull.Value;
        else
            Driver.BindTimestamp(parameter, utc.Value);

        command.Parameters.Add(parameter);
    }
}
