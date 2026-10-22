using DeNoise.Infrastructure.Health;
using DeNoise.Infrastructure.Observability;
using DeNoise.Infrastructure.Ops;
using DeNoise.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeNoise.Infrastructure.Hosting;

/// <summary>Cross-cutting wiring shared by the three hosts and the migrator.</summary>
public static class DeNoiseHostExtensions
{
    /// <summary>Time source, persistence, observability and the health checks every host exposes.</summary>
    public static WebApplicationBuilder AddDeNoiseCore(this WebApplicationBuilder builder, string component)
    {
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddDeNoisePersistence(builder.Configuration);
        builder.AddDeNoiseObservability($"denoise-{component}");
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
