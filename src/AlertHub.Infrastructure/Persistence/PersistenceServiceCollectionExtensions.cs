using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AlertHub.Infrastructure.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public const string ConnectionStringName = "AlertHub";

    public static IServiceCollection AddAlertHubPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured (ConnectionStrings__AlertHub).");
        return services.AddAlertHubPersistence(connectionString);
    }

    public static IServiceCollection AddAlertHubPersistence(this IServiceCollection services, string connectionString)
    {
        var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        services.AddSingleton(dataSource);
        services.AddDbContext<AlertHubDbContext>((sp, options) =>
        {
            options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npgsql =>
            {
                npgsql.MigrationsHistoryTable(AlertHubDbContext.MigrationsHistoryTable, AlertHubDbContext.MigrationsHistorySchema);
                npgsql.MigrationsAssembly(typeof(AlertHubDbContext).Assembly.FullName);
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
