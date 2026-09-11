using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AlertHub.Infrastructure.Persistence;

/// <summary>Used only by <c>dotnet ef migrations add</c>; no database connection is opened for scaffolding.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AlertHubDbContext>
{
    public AlertHubDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__AlertHub")
            ?? "Host=localhost;Database=alerthub;Username=postgres;Password=postgres";
        var options = new DbContextOptionsBuilder<AlertHubDbContext>()
            .UseNpgsql(cs, npgsql => npgsql.MigrationsHistoryTable(AlertHubDbContext.MigrationsHistoryTable, AlertHubDbContext.MigrationsHistorySchema))
            .Options;
        return new AlertHubDbContext(options);
    }
}
