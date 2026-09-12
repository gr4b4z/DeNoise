using AlertHub.Application.Retention;
using Microsoft.Extensions.Options;

namespace AlertHub.Workers.Scheduling;

/// <summary><c>retention_raw</c> + <c>retention_rows</c> (05 §7): daily at <c>Retention:RunAtUtcHour</c> UTC on the scheduler role. A failed run is retried the next day; <c>POST /api/v1/hub/retention/run</c> runs it on demand.</summary>
public sealed class RetentionWorker(IServiceScopeFactory scopes, IOptions<RetentionOptions> options, TimeProvider time, ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = time.GetUtcNow();
            var next = RetentionSchedule.NextRun(now, options.Value.RunAtUtcHour);
            logger.LogInformation("retention: next run at {Next:O}", next);
            try
            {
                await Task.Delay(next - now, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IRetentionRunner>().RunAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "retention run failed; next attempt tomorrow");
            }
        }
    }
}
