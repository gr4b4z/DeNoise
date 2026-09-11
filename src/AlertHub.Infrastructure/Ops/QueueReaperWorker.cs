using AlertHub.Application.Ops;
using AlertHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlertHub.Infrastructure.Ops;

/// <summary><c>job_reaper</c> and <c>outbox_reaper</c> (05 §7): every 30 s release reservations whose lease expired.</summary>
public sealed class QueueReaperWorker(IServiceScopeFactory scopes, IOptions<JobQueueOptions> options, TimeProvider time, ILogger<QueueReaperWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.ReapInterval, time);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var jobs = await scope.ServiceProvider.GetRequiredService<IJobQueue>().ReapExpiredAsync(stoppingToken);
                var now = time.GetUtcNow();
                var outbox = await scope.ServiceProvider.GetRequiredService<AlertHubDbContext>().Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE ops.outbox SET status = 'pending', reserved_by = NULL, reserved_until = NULL
                    WHERE status = 'reserved' AND reserved_until < {now}
                    """, stoppingToken);
                if (jobs + outbox > 0)
                {
                    logger.LogInformation("Reaper released {Jobs} job and {Outbox} outbox reservation(s)", jobs, outbox);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Reaper tick failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
