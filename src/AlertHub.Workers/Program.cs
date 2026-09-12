using AlertHub.Application.Ops;
using AlertHub.Domain.Ops;
using AlertHub.Infrastructure;
using AlertHub.Infrastructure.Health;
using AlertHub.Infrastructure.Hosting;
using AlertHub.Infrastructure.Observability;
using AlertHub.Infrastructure.Ops;
using AlertHub.Workers;
using AlertHub.Workers.Scheduling;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.AddAlertHubCore("workers");
builder.Services.AddAlertHubInfrastructure(builder.Configuration);

var rolesArg = args.FirstOrDefault(a => a.StartsWith("--roles=", StringComparison.OrdinalIgnoreCase))?["--roles=".Length..];
var roles = WorkerRoles.Parse(rolesArg ?? builder.Configuration[$"{WorkerRoles.Section}:Roles"]);
builder.Services.AddSingleton(roles);
builder.Services.AddHostedService<QueueGauges>();

if (roles.Processing)
{
    builder.Services.AddHostedService(sp => new JobRunner(
        sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<IOptions<JobQueueOptions>>(),
        sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<AlertHubMetrics>(), sp.GetRequiredService<ILogger<JobRunner>>(),
        JobKinds.Processing, "processing"));
}

if (roles.Scheduler)
{
    builder.Services.AddHostedService<PartitionCreateWorker>();
    builder.Services.AddHostedService<RetentionWorker>();
    builder.Services.AddHostedService<QueueReaperWorker>();
    builder.Services.AddHostedService<CoverageCheckWorker>();
    builder.Services.AddHostedService<HeartbeatCheckWorker>();
    builder.Services.AddHostedService<ApiProbeWorker>();
    builder.Services.AddOptions<DeadmanOptions>().Bind(builder.Configuration.GetSection(DeadmanOptions.Section));
    builder.Services.AddHttpClient(DeadmanPingWorker.Component);
    builder.Services.AddHostedService<DeadmanPingWorker>();
    builder.Services.AddHostedService(sp => new JobRunner(
        sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<IOptions<JobQueueOptions>>(),
        sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<AlertHubMetrics>(), sp.GetRequiredService<ILogger<JobRunner>>(),
        JobKinds.Scheduler, "scheduler"));
}

if (roles.Dispatcher)
{
    builder.Services.AddHostedService<AlertHub.Infrastructure.Notifications.DispatcherWorker>();
}

var app = builder.Build();
app.Logger.LogInformation("Workers host starting with roles {Roles}", roles);
app.UseAlertHubRequestLogging();
app.MapAlertHubHealth();

app.Run();

namespace AlertHub.Workers
{
    /// <summary>Assembly marker for <c>WebApplicationFactory</c> in tests.</summary>
    public sealed class WorkersHost;
}
