using AlertHub.Application.Abstractions;
using AlertHub.Application.Integrations;
using AlertHub.Domain.Integrations;
using AlertHub.Infrastructure;
using AlertHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Runs as a Helm pre-upgrade Job (ADR-10). App containers never migrate.
// Commands:  (none) → migrate;  seed-dev → migrate and create a development generic-webhook integration.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddAlertHubPersistence(builder.Configuration);
builder.Services.AddAlertHubInfrastructure(builder.Configuration);
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

    if (args.Contains("seed-dev", StringComparer.OrdinalIgnoreCase))
    {
        var teams = scope.ServiceProvider.GetRequiredService<AlertHub.Application.Teams.ITeamRepository>();
        if (await teams.GetTriageAsync() is null)
        {
            await scope.ServiceProvider.GetRequiredService<AlertHub.Application.Teams.TeamService>()
                .CreateAsync(new AlertHub.Application.Teams.CreateTeam("triage", ["dev"], IsTriage: true), Actor.System(Guid.NewGuid().ToString("N"), "migrator:seed-dev"));
            logger.LogInformation("Development triage team created");
        }

        var integrations = scope.ServiceProvider.GetRequiredService<IIntegrationRepository>();
        var existing = (await integrations.ListCurrentAsync()).FirstOrDefault(i => i.Name == "dev-generic-webhook");
        if (existing is not null)
        {
            logger.LogInformation("Development integration already exists (key {KeyId}); rotate it through the API to get a new token", existing.IngestKeyId);
        }
        else
        {
            var service = scope.ServiceProvider.GetRequiredService<IntegrationService>();
            var credentials = await service.CreateAsync(
                new CreateIntegration("dev-generic-webhook", IntegrationTypes.GenericWebhook, "dev"),
                Actor.System(Guid.NewGuid().ToString("N"), "migrator:seed-dev"));
            // Printed once to stdout on purpose (not through the logger) so it never lands in log aggregation.
            Console.Out.WriteLine($"Development integration created. POST {credentials.IngestPath}  Authorization: Bearer {credentials.IngestToken}");
        }
    }
    return 0;
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Migration failed");
    return 1;
}
