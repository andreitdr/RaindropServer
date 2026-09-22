using RaindropServer.Channels;
using RaindropServer.Data;
using RaindropServer.Data.Postgres;
using RaindropServer.Data.Sqlite;
using RaindropServer.Data.SqlServer;
using RaindropServer.Events;
using RaindropServer.Users;

namespace RaindropServer.Tests;

/// <summary>
/// The databases store tests run against: SQLite always, and PostgreSQL or SQL Server when a
/// connection string is given in <c>RAINDROP_TEST_POSTGRES</c> or <c>RAINDROP_TEST_SQLSERVER</c>
/// (the docker-compose services work). Tests use fresh ids, so sharing one database is fine.
/// </summary>
public static class TestDatabases
{
    public static TheoryData<string> Providers
    {
        get
        {
            var providers = new TheoryData<string> { "sqlite" };
            if (Environment.GetEnvironmentVariable("RAINDROP_TEST_POSTGRES") is { Length: > 0 })
                providers.Add("postgres");
            if (Environment.GetEnvironmentVariable("RAINDROP_TEST_SQLSERVER") is { Length: > 0 })
                providers.Add("sqlserver");
            return providers;
        }
    }

    /// <summary>A driver with its schema created, ready to make stores.</summary>
    public static async Task<(IDatabaseDriver Driver, DatabaseOptions Options)> OpenAsync(string provider)
    {
        IDatabaseDriver driver = provider switch
        {
            "sqlite" => new SqliteDatabaseDriver(),
            "postgres" => new PostgresDatabaseDriver(),
            "sqlserver" => new SqlServerDatabaseDriver(),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
        var connection = provider switch
        {
            "sqlite" => $"Data Source={Path.Combine(Path.GetTempPath(), $"raindrop-test-{Guid.NewGuid():N}.db")}",
            "postgres" => Environment.GetEnvironmentVariable("RAINDROP_TEST_POSTGRES")!,
            _ => Environment.GetEnvironmentVariable("RAINDROP_TEST_SQLSERVER")!
        };
        var options = new DatabaseOptions { Provider = provider, ConnectionString = connection };
        await driver.EnsureSchemaAsync(options, CancellationToken.None);
        return (driver, options);
    }
}

/// <summary>MLS messages and a device certificate made by the Rust core.</summary>
public static class Fixtures
{
    /// <summary>The channel whose group the fixtures belong to.</summary>
    public static readonly Guid ChannelId = Guid.Parse("6f1c2d3e-4a5b-4c6d-8e7f-001122334455");

    /// <summary>The user and device the certificate fixture is for.</summary>
    public static readonly Guid CertificateUserId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
    public static readonly Guid CertificateDeviceId = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");

    public static byte[] Load(string name) =>
        Convert.FromHexString(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"{name}.hex")).Trim());
}

/// <summary>Records what was published, instead of sending it anywhere.</summary>
public sealed class RecordingPublisher : IEventPublisher
{
    public List<(string Type, object? Data, EventAudience? Audience)> Published { get; } = [];

    public Task PublishAsync<T>(string type, T data, EventAudience? audience = null)
    {
        lock (Published) Published.Add((type, data, audience));
        return Task.CompletedTask;
    }

    public Task PublishPerRecipientAsync<T>(string type, T data, EventProjection<T> project) where T : class
    {
        lock (Published) Published.Add((type, data, null));
        return Task.CompletedTask;
    }
}

public static class Sessions
{
    public static UserSession For(Guid userId, Guid? deviceId = null) =>
        new($"token-{Guid.NewGuid():N}", userId, "user", DateTime.UtcNow, DateTime.UtcNow.AddHours(1), deviceId);

    public static async Task<UserRecord> ActiveUserAsync(IUserStore users, Guid? userId = null)
    {
        var name = $"u{Guid.NewGuid():N}"[..20];
        var user = new UserRecord
        {
            UserId = userId ?? Guid.NewGuid(),
            Username = name,
            UsernameNormalized = name,
            PasswordHash = "x",
            Status = UserStatus.Active,
            CreatedAtUtc = DateTime.UtcNow
        };
        await users.InsertAsync(user, CancellationToken.None);
        return user;
    }

    public static async Task<ChannelRecord> ChannelAsync(IChannelStore channels, Guid channelId, ChannelType type = ChannelType.Text)
    {
        var name = $"c{Guid.NewGuid():N}"[..20];
        var channel = new ChannelRecord
        {
            ChannelId = channelId,
            Type = type,
            Name = name,
            NameNormalized = name,
            CreatedBy = Guid.NewGuid(),
            CreatedAtUtc = DateTime.UtcNow
        };
        await channels.InsertAsync(channel, CancellationToken.None);
        return channel;
    }
}
