using AlertHub.Infrastructure;
using AlertHub.Infrastructure.Health;
using AlertHub.Infrastructure.Hosting;
using AlertHub.Infrastructure.Observability;

var builder = WebApplication.CreateBuilder(args);
builder.AddAlertHubCore("api");
builder.Services.AddAlertHubInfrastructure(builder.Configuration);
builder.Services.AddHealthChecks().AddCheck<OutboxLagHealthCheck>("outbox-lag", tags: [HealthEndpoints.ReadyTag]);
builder.Services.AddOpenApi();

var app = builder.Build();
app.UseAlertHubRequestLogging();
app.MapAlertHubHealth();
app.MapOpenApi("/openapi/v1.json");

app.Run();

namespace AlertHub.Api
{
    /// <summary>Assembly marker for <c>WebApplicationFactory</c> in tests.</summary>
    public sealed class ApiHost;
}
