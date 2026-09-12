using DeNoise.Application.Ops;
using DeNoise.Domain.Ops;
using DeNoise.Infrastructure;
using DeNoise.Infrastructure.Health;
using DeNoise.Infrastructure.Hosting;
using DeNoise.Infrastructure.Observability;
using DeNoise.Infrastructure.Ops;
using DeNoise.Workers;
using DeNoise.Workers.Scheduling;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.AddDeNoiseCore("workers");
builder.Services.AddDeNoiseInfrastructure(builder.Configuration);

var rolesArg = args.FirstOrDefault(a => a.StartsWith("--roles=", StringComparison.OrdinalIgnoreCase))?["--roles=".Length..];
var roles = WorkerRoles.Parse(rolesArg ?? builder.Configuration[$"{WorkerRoles.Section}:Roles"]);
builder.Services.AddSingleton(roles);
builder.Services.AddHostedService<QueueGauges>();

if (roles.Processing)
{
    builder.Services.AddHostedService(sp => new JobRunner(
        sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<IOptions<JobQueueOptions>>(),
        sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<DeNoiseMetrics>(), sp.GetRequiredService<ILogger<JobRunner>>(),
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
        sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<DeNoiseMetrics>(), sp.GetRequiredService<ILogger<JobRunner>>(),
        JobKinds.Scheduler, "scheduler"));
}

if (roles.Dispatcher)
{
    builder.Services.AddHostedService<DeNoise.Infrastructure.Notifications.DispatcherWorker>();
}

var app = builder.Build();
app.Logger.LogInformation("Workers host starting with roles {Roles}", roles);
app.UseDeNoiseRequestLogging();
app.MapDeNoiseHealth();

app.Run();

namespace DeNoise.Workers
{
    /// <summary>Assembly marker for <c>WebApplicationFactory</c> in tests.</summary>
    public sealed class WorkersHost;
}
