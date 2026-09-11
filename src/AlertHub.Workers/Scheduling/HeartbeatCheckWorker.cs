using AlertHub.Application.Heartbeats;

namespace AlertHub.Workers.Scheduling;

/// <summary><c>heartbeat_check</c> (04 §10): every 30 s the scheduler detects late and missed heartbeats and applies maintenance auto-pause. Missing is detected here, never by a ping.</summary>
public sealed class HeartbeatCheckWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<HeartbeatCheckWorker> logger) : BackgroundService
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
                await scope.ServiceProvider.GetRequiredService<HeartbeatMonitor>().TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "heartbeat_check failed; will retry on next tick");
            }
        }
    }
}
