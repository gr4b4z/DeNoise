using DeNoise.Domain.Ops;
using DeNoise.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace DeNoise.Infrastructure.Health;

public sealed class HealthOptions
{
    public const string Section = "Health";
    /// <summary>Oldest pending outbox row age above which the API host reports not-ready (06 §8).</summary>
    public TimeSpan OutboxLagThreshold { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>Readiness on the API host: oldest pending outbox row is younger than the threshold.</summary>
public sealed class OutboxLagHealthCheck(DeNoiseDbContext db, TimeProvider time, IOptions<HealthOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var oldest = await db.Outbox.AsNoTracking()
            .Where(o => o.Status == OutboxStatus.Pending)
            .OrderBy(o => o.NotBefore)
            .Select(o => (DateTimeOffset?)o.NotBefore)
            .FirstOrDefaultAsync(cancellationToken);
        if (oldest is null) return HealthCheckResult.Healthy("outbox empty");

        var lag = time.GetUtcNow() - oldest.Value;
        var data = new Dictionary<string, object> { ["lagSeconds"] = Math.Max(0, lag.TotalSeconds) };
        return lag <= options.Value.OutboxLagThreshold
            ? HealthCheckResult.Healthy($"outbox lag {lag.TotalSeconds:F0}s", data)
            : HealthCheckResult.Degraded($"outbox lag {lag.TotalSeconds:F0}s exceeds {options.Value.OutboxLagThreshold}", data: data);
    }
}
