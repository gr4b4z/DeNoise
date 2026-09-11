using AlertHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Runs as a Helm pre-upgrade Job (ADR-10). App containers never migrate.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddAlertHubPersistence(builder.Configuration);
using var host = builder.Build();

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Migrator");
try
{
    await using var scope = host.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AlertHubDbContext>();
    var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
    logger.LogInformation("Applying {Count} pending migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
    await db.Database.MigrateAsync();

    var created = await scope.ServiceProvider.GetRequiredService<RawEventPartitions>().EnsureAsync();
    logger.LogInformation("Migration complete; {Created} raw_event partition(s) created", created);
    return 0;
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Migration failed");
    return 1;
}
