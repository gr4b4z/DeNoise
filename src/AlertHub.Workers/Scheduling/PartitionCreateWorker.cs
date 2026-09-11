using AlertHub.Infrastructure.Persistence;

namespace AlertHub.Workers.Scheduling;

/// <summary>
/// <c>partition_create</c> maintenance job (05 §7): creates <c>alert.raw_event</c> partitions 7 days ahead,
/// on startup and then daily. Idempotent; safe to run on every scheduler replica.
/// </summary>
public sealed class PartitionCreateWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<PartitionCreateWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24), time);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var created = await scope.ServiceProvider.GetRequiredService<RawEventPartitions>().EnsureAsync(ct: stoppingToken);
                logger.LogInformation("partition_create: {Created} raw_event partition(s) created", created);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "partition_create failed; will retry on next tick");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
