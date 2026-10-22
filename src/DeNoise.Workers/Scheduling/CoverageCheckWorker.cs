using DeNoise.Application.Coverage;

namespace DeNoise.Workers.Scheduling;

/// <summary>
/// <c>coverage_check</c> (04 §11): every 30 s the time-driven coverage transitions (healthy → delayed → degraded → unavailable, 04 §9)
/// are evaluated for every integration with a canary method. Signal-driven transitions happen in processing.
/// </summary>
public sealed class CoverageCheckWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<CoverageCheckWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var changed = await scope.ServiceProvider.GetRequiredService<CoverageEvaluator>().TickAllAsync(stoppingToken);
                foreach (var c in changed)
                {
                    logger.LogWarning("coverage of integration {IntegrationId}: {From} → {To} ({Suspended} closure(s) suspended)", c.IntegrationId, c.Step.From, c.Step.To, c.SuspendedJobs);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "coverage_check failed; will retry on next tick");
            }
        }
    }
}
