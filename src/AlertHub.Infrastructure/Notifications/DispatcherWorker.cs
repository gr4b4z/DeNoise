using AlertHub.Application.Notifications;
using AlertHub.Application.Ops;
using AlertHub.Infrastructure.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlertHub.Infrastructure.Notifications;

/// <summary>Dispatcher role host loop: one DI scope per batch, idle poll when the outbox is empty.</summary>
public sealed class DispatcherWorker(IServiceScopeFactory scopes, IOptions<JobQueueOptions> options, TimeProvider time, AlertHubMetrics metrics, ILogger<DispatcherWorker> logger) : BackgroundService
{
    public string WorkerId { get; } = $"dispatcher@{Environment.GetEnvironmentVariable("HOSTNAME") ?? Environment.MachineName}#{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Dispatcher {WorkerId} starting", WorkerId);
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                processed = await RunBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Dispatcher batch failed");
            }
            if (processed == 0)
            {
                try
                {
                    await Task.Delay(options.Value.IdlePoll, time, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    public async Task<int> RunBatchAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();
        var results = await dispatcher.DispatchBatchAsync(WorkerId, options.Value.BatchSize, ct);
        foreach (var (message, outcome) in results)
        {
            metrics.DeliveryAttempts.Add(1, new KeyValuePair<string, object?>("channel", "outbox"), new KeyValuePair<string, object?>("outcome", outcome.ToString().ToLowerInvariant()));
            if (outcome == DispatchOutcome.Sent)
            {
                metrics.NotificationLatency.Record((time.GetUtcNow() - message.CreatedAt).TotalSeconds, new KeyValuePair<string, object?>("type", message.Type));
            }
        }
        return results.Count;
    }
}
