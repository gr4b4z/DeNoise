using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DeNoise.Infrastructure.Persistence;

/// <summary>Used only by <c>dotnet ef migrations add</c>; no database connection is opened for scaffolding.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DeNoiseDbContext>
{
    public DeNoiseDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__DeNoise")
            ?? "Host=localhost;Database=denoise;Username=postgres;Password=postgres";
        var options = new DbContextOptionsBuilder<DeNoiseDbContext>()
            .UseNpgsql(cs, npgsql => npgsql.MigrationsHistoryTable(DeNoiseDbContext.MigrationsHistoryTable, DeNoiseDbContext.MigrationsHistorySchema))
            .Options;
        return new DeNoiseDbContext(options);
    }
}
