using DeNoise.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DeNoise.Infrastructure.Health;

/// <summary>Readiness: database reachable and every known migration applied (06 §8). App containers never migrate (ADR-10).</summary>
public sealed class DatabaseHealthCheck(DeNoiseDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await db.Database.CanConnectAsync(cancellationToken))
            {
                return HealthCheckResult.Unhealthy("database unreachable");
            }

            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            return pending.Count == 0
                ? HealthCheckResult.Healthy("database reachable, schema current")
                : HealthCheckResult.Unhealthy($"schema behind: {pending.Count} pending migration(s), first '{pending[0]}'");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("database check failed", ex);
        }
    }
}
