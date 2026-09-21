namespace RaindropServer.Data;

/// <summary>
/// Which database module the server owner has enabled, and how to reach their database.
/// Nothing talks to a database until one of these is supplied.
/// </summary>
public sealed class DatabaseOptions
{
    /// <summary>
    /// The name of the driver module to use, matched against <see cref="IDatabaseDriver.Name"/>
    /// case-insensitively, e.g. "sqlite", "postgres" or "sqlserver".
    /// </summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>The ADO.NET connection string, in whatever form the chosen driver expects.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Whether the server may create its tables and indexes at startup. Turn this off when the
    /// schema is managed by the DBA and the server's account is not allowed to run DDL.
    /// </summary>
    public bool AutoCreateSchema { get; set; } = true;

    /// <summary>Throws when the options cannot possibly work, before anything tries to connect.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Provider))
            throw new InvalidOperationException(
                "No database provider was configured. Set the provider to one of the installed drivers: " +
                $"{DatabaseDriverRegistry.DescribeAvailable()}.");

        if (string.IsNullOrWhiteSpace(ConnectionString))
            throw new InvalidOperationException($"No connection string was configured for the '{Provider}' database driver.");
    }
}
