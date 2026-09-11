using System.Diagnostics;
using AlertHub.Application.Ops;
using AlertHub.Domain.Ops;
using AlertHub.Infrastructure.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlertHub.Infrastructure.Ops;

/// <summary>
/// Claims due jobs of the kinds it has handlers for and runs them one at a time per claimed batch.
/// Each job gets its own DI scope (its own DbContext/transaction); the handler's own commit is separate from
/// marking the job done, which is why handlers must be idempotent (a crash between the two re-runs the job).
/// </summary>
public sealed class JobRunner(
    IServiceScopeFactory scopes, IEnumerable<IJobHandler> handlers, IOptions<JobQueueOptions> options,
    TimeProvider time, AlertHubMetrics metrics, ILogger<JobRunner> logger, IReadOnlyCollection<string> kinds, string role) : BackgroundService
{
    private readonly Dictionary<string, IJobHandler> _handlers = handlers.Where(h => kinds.Contains(h.Kind)).ToDictionary(h => h.Kind);

    public string WorkerId { get; } = $"{role}@{Environment.GetEnvironmentVariable("HOSTNAME") ?? Environment.MachineName}#{Guid.NewGuid():N}";

    public IReadOnlyCollection<string> HandledKinds => _handlers.Keys;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_handlers.Count == 0)
        {
            logger.LogWarning("JobRunner {Role} has no handlers for kinds {Kinds}; idle", role, string.Join(",", kinds));
            return;
        }
        logger.LogInformation("JobRunner {Role} ({WorkerId}) handling {Kinds}", role, WorkerId, string.Join(",", _handlers.Keys));

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
                logger.LogError(ex, "JobRunner {Role} batch failed", role);
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

    /// <summary>Claims and runs one batch; exposed for tests that drive the runner synchronously.</summary>
    public async Task<int> RunBatchAsync(CancellationToken ct)
    {
        IReadOnlyList<Job> batch;
        await using (var claimScope = scopes.CreateAsyncScope())
        {
            var queue = claimScope.ServiceProvider.GetRequiredService<IJobQueue>();
            batch = await queue.ClaimAsync(_handlers.Keys.ToArray(), WorkerId, options.Value.Lease, options.Value.BatchSize, ct);
        }

        foreach (var job in batch)
        {
            await RunOneAsync(job, ct);
        }
        return batch.Count;
    }

    private async Task RunOneAsync(Job job, CancellationToken ct)
    {
        var handler = _handlers[job.Kind];
        var started = Stopwatch.GetTimestamp();
        metrics.SchedulerLag.Record(Math.Max(0, (time.GetUtcNow() - job.NotBefore).TotalSeconds), new KeyValuePair<string, object?>("kind", job.Kind));

        try
        {
            await using (var scope = scopes.CreateAsyncScope())
            {
                var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
                var context = new JobContext(WorkerId, (lease, token) => queue.ExtendAsync(job.JobId, WorkerId, lease, token));
                await handler.HandleAsync(job, context, ct);
            }

            await using (var scope = scopes.CreateAsyncScope())
            {
                var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
                if (!await queue.CompleteAsync(job.JobId, WorkerId, ct))
                {
                    logger.LogWarning("Job {JobId} ({Kind}) finished but its reservation was lost; it will be re-run", job.JobId, job.Kind);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // shutdown: leave the reservation to expire and be reaped
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobId} ({Kind}) attempt {Attempt} failed", job.JobId, job.Kind, job.Attempts);
            await using var scope = scopes.CreateAsyncScope();
            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            var outcome = await queue.FailAsync(job.JobId, WorkerId, ex.ToString(), CancellationToken.None);
            if (outcome == JobFailureOutcome.MovedToFailureQueue)
            {
                logger.LogError("Job {JobId} ({Kind}) exhausted {MaxAttempts} attempts and is in the failure queue", job.JobId, job.Kind, job.MaxAttempts);
            }
        }
        finally
        {
            logger.LogDebug("Job {JobId} ({Kind}) took {ElapsedMs} ms", job.JobId, job.Kind, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
}
