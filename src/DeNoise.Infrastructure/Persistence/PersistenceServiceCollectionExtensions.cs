using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeNoise.Infrastructure.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public const string ConnectionStringName = "DeNoise";

    public static IServiceCollection AddDeNoisePersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured (ConnectionStrings__DeNoise).");
        return services.AddDeNoisePersistence(connectionString);
    }

    public static IServiceCollection AddDeNoisePersistence(this IServiceCollection services, string connectionString)
    {
        var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        services.AddSingleton(dataSource);
        services.AddDbContext<DeNoiseDbContext>((sp, options) =>
        {
            options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npgsql =>
            {
                npgsql.MigrationsHistoryTable(DeNoiseDbContext.MigrationsHistoryTable, DeNoiseDbContext.MigrationsHistorySchema);
                npgsql.MigrationsAssembly(typeof(DeNoiseDbContext).Assembly.FullName);
            });
            options.UseSnakeCaseNamingConventionIfAvailable();
        });
        services.AddScoped<RawEventPartitions>();
        return services;
    }

    // Column names are set explicitly in every configuration; no naming-convention package is needed.
    private static void UseSnakeCaseNamingConventionIfAvailable(this DbContextOptionsBuilder _)
    {
    }
}
