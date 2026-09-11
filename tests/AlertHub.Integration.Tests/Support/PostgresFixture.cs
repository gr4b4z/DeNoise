using AlertHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AlertHub.Integration.Tests.Support;

/// <summary>
/// One PostgreSQL server per test run. Uses Testcontainers by default; when <c>ALERTHUB_TEST_CONNECTION</c>
/// is set (environments without Docker), a throw-away database is created on that server instead.
/// Each test class gets its own database via <see cref="CreateDatabaseAsync"/> so classes can run in parallel.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string _adminConnectionString = string.Empty;
    private readonly List<string> _databases = [];

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("ALERTHUB_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(external))
        {
            _adminConnectionString = external;
            return;
        }

        _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _container.StartAsync();
        _adminConnectionString = _container.GetConnectionString();
    }

    /// <summary>Creates an empty database, applies all migrations and ensures raw_event partitions; returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync(string prefix = "t")
    {
        var name = $"alerthub_{prefix}_{Guid.NewGuid():N}"[..40].ToLowerInvariant();
        await using (var admin = new NpgsqlConnection(_adminConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{name}\"";
            await cmd.ExecuteNonQueryAsync();
        }
        _databases.Add(name);

        var cs = new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = name }.ConnectionString;
        await using var db = CreateContext(cs);
        await db.Database.MigrateAsync();
        await new RawEventPartitions(db, TimeProvider.System, NullLogger<RawEventPartitions>.Instance).EnsureAsync();
        return cs;
    }

    public static AlertHubDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AlertHubDbContext>()
            .UseNpgsql(connectionString, n => n.MigrationsHistoryTable(AlertHubDbContext.MigrationsHistoryTable, AlertHubDbContext.MigrationsHistorySchema))
            .Options;
        return new AlertHubDbContext(options);
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            return;
        }

        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(_adminConnectionString);
        await admin.OpenAsync();
        foreach (var name in _databases)
        {
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)";
            await cmd.ExecuteNonQueryAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
