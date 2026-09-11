using AlertHub.Infrastructure.Health;
using AlertHub.Infrastructure.Observability;
using AlertHub.Infrastructure.Ops;
using AlertHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlertHub.Infrastructure.Hosting;

/// <summary>Cross-cutting wiring shared by the three hosts and the migrator.</summary>
public static class AlertHubHostExtensions
{
    /// <summary>Time source, persistence, observability and the health checks every host exposes.</summary>
    public static WebApplicationBuilder AddAlertHubCore(this WebApplicationBuilder builder, string component)
    {
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddAlertHubPersistence(builder.Configuration);
        builder.AddAlertHubObservability($"alerthub-{component}");
        builder.Services.AddOptions<HealthOptions>().Bind(builder.Configuration.GetSection(HealthOptions.Section));
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(), tags: [HealthEndpoints.LiveTag])
            .AddCheck<DatabaseHealthCheck>("database", tags: [HealthEndpoints.ReadyTag, HealthEndpoints.StartupTag]);
        builder.Services.AddHostedService(sp => new ComponentHeartbeatService(
            sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<ComponentHeartbeatService>>(), component));
        return builder;
    }
}
