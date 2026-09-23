using System.Data.Common;
using RaindropServer.Configuration;

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

    /// <summary>
    /// Development only: drops every Raindrop table before the schema is (re)created, so the server
    /// starts from an empty database every time. Never set this where the database holds anything
    /// worth keeping — there is no confirmation and no undo.
    /// </summary>
    public bool WipeOnStartup { get; set; }

    /// <summary>Throws when the options cannot possibly work, before anything tries to connect.</summary>
    /// <exception cref="InvalidOperationException">A required setting is missing or malformed.</exception>
    public void Validate()
    {
        var report = new ConfigurationReport().Section("database");
        Validate(report);
        report.ThrowIfInvalid();
    }

    /// <summary>
    /// Reports what can be told before connecting: that a driver by that name is installed, and that
    /// the connection string is at least a connection string. Whether the database is there and will
    /// have this server is settled by connecting to it, which happens moments later.
    /// </summary>
    public void Validate(ConfigurationReport report)
    {
        if (report.Require("provider", Provider,
                "is required: nothing is stored until a database module is enabled.",
                $"Installed drivers: {DatabaseDriverRegistry.DescribeAvailable()}."))
        {
            var installed = DatabaseDriverRegistry.Available.Select(driver => driver.Name).ToList();

            if (!installed.Contains(Provider.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                var suggestion = ConfigurationNames.Suggest(Provider.Trim(), installed);

                report.Error("provider",
                    $"is '{Provider}', which is not an installed database driver.",
                    suggestion is null
                        ? $"Installed drivers: {DatabaseDriverRegistry.DescribeAvailable()}."
                        : $"Did you mean '{suggestion}'? Installed drivers: {DatabaseDriverRegistry.DescribeAvailable()}.");
            }
        }

        if (!report.Require("connectionString", ConnectionString,
                $"is required: the '{Provider}' driver has no way to reach a database without one."))
            return;

        try
        {
            // Every ADO.NET provider parses its own connection string, but they all share this
            // grammar — so a stray quote or a missing semicolon is worth catching here rather than
            // as whatever the driver makes of it.
            _ = new DbConnectionStringBuilder { ConnectionString = ConnectionString };
        }
        catch (ArgumentException ex)
        {
            report.Error("connectionString", $"cannot be parsed: {ex.Message}");
        }

        if (!AutoCreateSchema)
            report.Warn("autoCreateSchema",
                "is off, so this server will not create any table it is missing.",
                "Only correct when the schema is managed separately; a fresh database will otherwise fail every request.");

        if (WipeOnStartup)
            report.Warn("wipeOnStartup",
                "is set, so this server will drop every table and lose all data each time it starts.",
                "Development only — never set this where the database holds anything worth keeping.");
    }
}
