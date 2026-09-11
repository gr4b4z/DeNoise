using AlertHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlertHub.Infrastructure.Ops;

/// <summary>Upserts <c>ops.hub_component_heartbeat</c> for this component every 30 s; consumed by the hub health screen and self-monitoring (spec §13.7).</summary>
public sealed class ComponentHeartbeatService(
    IServiceScopeFactory scopes, TimeProvider time, ILogger<ComponentHeartbeatService> logger, string component) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private readonly string _instance = Environment.GetEnvironmentVariable("HOSTNAME") ?? Environment.MachineName;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AlertHubDbContext>();
                var now = time.GetUtcNow();
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO ops.hub_component_heartbeat (component, instance, last_seen)
                    VALUES ({component}, {_instance}, {now})
                    ON CONFLICT (component) DO UPDATE SET instance = EXCLUDED.instance, last_seen = EXCLUDED.last_seen
                    """, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Component heartbeat for {Component} failed", component);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
