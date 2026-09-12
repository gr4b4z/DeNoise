using System.Diagnostics.Metrics;
using DeNoise.Infrastructure.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DeNoise.Infrastructure.Ops;

/// <summary>
/// Feeds <c>denoise_job_queue_depth{kind}</c> and <c>denoise_job_oldest_age_seconds{kind}</c> (ADR-9) from a
/// periodic aggregate query; observable gauges read the last snapshot so metric collection never touches the DB.
/// </summary>
public sealed class QueueGauges(IServiceScopeFactory scopes, DeNoiseMetrics metrics, TimeProvider time, ILogger<QueueGauges> logger) : BackgroundService
{
    private volatile IReadOnlyList<(string Kind, long Depth, double OldestAge)> _snapshot = [];

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        metrics.RegisterGauge(GaugeNames.JobQueueDepth, () => _snapshot.Select(s => new Measurement<long>(s.Depth, new KeyValuePair<string, object?>("kind", s.Kind))));
        metrics.RegisterGauge(GaugeNames.JobOldestAgeSeconds, () => _snapshot.Select(s => new Measurement<double>(s.OldestAge, new KeyValuePair<string, object?>("kind", s.Kind))), "s");
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), time);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
                await using var cmd = dataSource.CreateCommand("""
                    SELECT kind, count(*)::bigint, extract(epoch FROM (now() - min(not_before)))::float8
                    FROM ops.job WHERE status = 'pending' GROUP BY kind
                    """);
                await using var reader = await cmd.ExecuteReaderAsync(stoppingToken);
                var rows = new List<(string, long, double)>();
                while (await reader.ReadAsync(stoppingToken))
                {
                    rows.Add((reader.GetString(0), reader.GetInt64(1), Math.Max(0, reader.GetDouble(2))));
                }
                _snapshot = rows;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Queue gauge refresh failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
