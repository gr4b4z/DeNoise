using AlertHub.Infrastructure.Health;
using AlertHub.Infrastructure.Hosting;
using AlertHub.Infrastructure.Observability;
using AlertHub.Workers;
using AlertHub.Workers.Scheduling;

var builder = WebApplication.CreateBuilder(args);
builder.AddAlertHubCore("workers");

var rolesArg = args.FirstOrDefault(a => a.StartsWith("--roles=", StringComparison.OrdinalIgnoreCase))?["--roles=".Length..];
var roles = WorkerRoles.Parse(rolesArg ?? builder.Configuration[$"{WorkerRoles.Section}:Roles"]);
builder.Services.AddSingleton(roles);

if (roles.Scheduler)
{
    builder.Services.AddHostedService<PartitionCreateWorker>();
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
