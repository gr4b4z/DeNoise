using System.Collections.Concurrent;
using System.Text;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Ingest;
using AlertHub.Application.Integrations;
using AlertHub.Application.Ops;
using AlertHub.Domain.Ops;
using AlertHub.Infrastructure.Observability;
using AlertHub.Infrastructure.Ops;
using AlertHub.Infrastructure.Persistence;
using AlertHub.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AlertHub.Integration.Tests.Milestone1;

/// <summary>Spec §22: <i>Worker fails during processing</i> and <i>Worker fails after state commit</i> (job part; the outbox part is milestone 3).</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class WorkerFailureScenarios(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    private string _cs = string.Empty;
    private FakeTimeProvider _time = null!;
    private ScriptedHandler _handler = null!;
    private ServiceProvider _services = null!;
    private IntegrationCredentials _integration = null!;

    public async Task InitializeAsync()
    {
        _cs = await postgres.CreateDatabaseAsync("workerfail");
        _time = new FakeTimeProvider(T0);
        _handler = new ScriptedHandler();
        _services = TestServices.Build(_cs, _time, s => { s.RemoveAll<IJobHandler>(); s.AddSingleton<IJobHandler>(_handler); },
            new Dictionary<string, string?> { ["JobQueue:Lease"] = "00:02:00", ["JobQueue:BatchSize"] = "10" });
        _integration = await TestServices.CreateIntegrationAsync(_services);
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    private JobRunner NewRunner(string role = "processing") => new(
        _services.GetRequiredService<IServiceScopeFactory>(), _services.GetRequiredService<IOptions<JobQueueOptions>>(),
        _time, _services.GetRequiredService<AlertHubMetrics>(), _services.GetRequiredService<ILogger<JobRunner>>(), JobKinds.Processing, role);

    private async Task<Guid> IngestAsync(string body)
    {
        var accepted = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<IngestService>()
            .AcceptAsync(_integration.Integration, new IngestRequest(Encoding.UTF8.GetBytes(body), "application/json", new Dictionary<string, string>(), null)));
        return accepted.EventId;
    }

    private async Task<Job> JobForAsync(Guid eventId)
    {
        var jobs = await TestServices.InDbAsync(_services, db => db.Jobs.AsNoTracking().Where(j => j.IntegrationId == _integration.Integration.IntegrationId).ToListAsync());
        return jobs.Single(j => j.Payload.Contains(eventId.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Scenario_WorkerFailsDuringProcessing_throwing_handler_is_retried_and_raw_event_is_untouched()
    {
        var eventId = await IngestAsync("""{"n":1}""");
        _handler.Script.Enqueue(_ => throw new InvalidOperationException("simulated crash mid-processing"));
        _handler.Script.Enqueue(_ => Task.CompletedTask);
        var runner = NewRunner();

        (await runner.RunBatchAsync(CancellationToken.None)).Should().Be(1);
        var afterCrash = await JobForAsync(eventId);
        afterCrash.Status.Should().Be(JobStatus.Pending, "the failure was recorded and the job rescheduled");
        afterCrash.Attempts.Should().Be(1);
        afterCrash.LastError.Should().Contain("simulated crash");

        (await runner.RunBatchAsync(CancellationToken.None)).Should().Be(0, "backoff has not elapsed");
        _time.Advance(Backoff.Base);
        (await runner.RunBatchAsync(CancellationToken.None)).Should().Be(1);

        var done = await JobForAsync(eventId);
        done.Status.Should().Be(JobStatus.Done);
        done.Attempts.Should().Be(2);
        _handler.Invocations.Should().HaveCount(2).And.OnlyContain(id => id == afterCrash.JobId);

        await using var db = PostgresFixture.CreateContext(_cs);
        (await db.RawEvents.CountAsync(r => r.EventId == eventId)).Should().Be(1, "the accepted event is never lost");
    }

    [Fact]
    public async Task Scenario_WorkerFailsDuringProcessing_worker_dies_after_claim_reservation_is_reaped_and_job_reruns()
    {
        var eventId = await IngestAsync("""{"n":2}""");

        // Worker A claims and vanishes (never completes, never fails).
        await TestServices.InQueueAsync(_services, q => q.ClaimAsync(JobKinds.Processing, "worker-a", TimeSpan.FromMinutes(2), 10));
        var reserved = await JobForAsync(eventId);
        reserved.Status.Should().Be(JobStatus.Reserved);

        // Worker B sees nothing while the lease is alive.
        _handler.Script.Enqueue(_ => Task.CompletedTask);
        var runnerB = NewRunner("processing-b");
        (await runnerB.RunBatchAsync(CancellationToken.None)).Should().Be(0);

        // Lease expires, reaper releases, B runs it.
        _time.Advance(TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(1)));
        (await TestServices.InQueueAsync(_services, q => q.ReapExpiredAsync())).Should().Be(1);
        (await runnerB.RunBatchAsync(CancellationToken.None)).Should().Be(1);

        var done = await JobForAsync(eventId);
        done.Status.Should().Be(JobStatus.Done);
        done.Attempts.Should().Be(2);
        _handler.Invocations.Should().ContainSingle();
    }

    [Fact]
    public async Task Scenario_WorkerFailsAfterStateCommit_job_is_rerun_and_the_stale_worker_cannot_mark_it_done()
    {
        var eventId = await IngestAsync("""{"n":3}""");
        var commits = new ConcurrentBag<Guid>();

        // Attempt 1: the handler commits its state (an audit row stands in for the episode transition), then the
        // worker dies before CompleteAsync — simulated by expiring the lease inside the handler.
        _handler.Script.Enqueue(async job =>
        {
            await TestServices.InScopeAsync(_services, async sp =>
            {
                var db = sp.GetRequiredService<AlertHubDbContext>();
                db.AuditEntries.Add(new Domain.Audit.AuditEntry
                {
                    Id = Guid.CreateVersion7(),
                    At = _time.GetUtcNow(),
                    ActorType = "system",
                    ActorId = "test",
                    Action = "test.commit",
                    TargetType = "job",
                    TargetId = job.JobId.ToString(),
                    CorrelationId = "scenario",
                });
                await db.SaveChangesAsync();
            });
            commits.Add(job.JobId);
            _time.Advance(TimeSpan.FromMinutes(3)); // worker stalls past its lease
        });
        _handler.Script.Enqueue(_ => Task.CompletedTask);

        var runner = NewRunner();
        (await runner.RunBatchAsync(CancellationToken.None)).Should().Be(1);

        var afterStall = await JobForAsync(eventId);
        afterStall.Status.Should().Be(JobStatus.Reserved, "the stale worker's CompleteAsync was rejected by the reservation check");
        commits.Should().ContainSingle();

        (await TestServices.InQueueAsync(_services, q => q.ReapExpiredAsync())).Should().Be(1);
        (await runner.RunBatchAsync(CancellationToken.None)).Should().Be(1);

        var done = await JobForAsync(eventId);
        done.Status.Should().Be(JobStatus.Done);
        _handler.Invocations.Should().HaveCount(2, "the job re-ran; handlers must be idempotent");

        await using var db = PostgresFixture.CreateContext(_cs);
        (await db.AuditEntries.CountAsync(a => a.Action == "test.commit")).Should().Be(1, "the committed state survived the crash");
    }

    [Fact]
    public async Task Job_exhausting_attempts_lands_in_the_failure_queue_and_can_be_retried()
    {
        var eventId = await IngestAsync("""{"n":4}""");
        var target = await JobForAsync(eventId);
        await TestServices.InDbAsync(_services, db => db.Jobs.Where(j => j.JobId == target.JobId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.MaxAttempts, 2)));
        for (var i = 0; i < 3; i++) _handler.Script.Enqueue(_ => throw new InvalidOperationException("always"));
        var runner = NewRunner();

        (await runner.RunBatchAsync(CancellationToken.None)).Should().Be(1);
        _time.Advance(Backoff.Base);
        (await runner.RunBatchAsync(CancellationToken.None)).Should().Be(1);

        var failed = await JobForAsync(eventId);
        failed.Status.Should().Be(JobStatus.Failed);
        (await runner.RunBatchAsync(CancellationToken.None)).Should().Be(0, "failed jobs are not claimed");

        (await TestServices.InQueueAsync(_services, q => q.RetryFailedAsync(failed.JobId))).Should().BeTrue();
        (await JobForAsync(eventId)).Status.Should().Be(JobStatus.Pending);
    }

    private sealed class ScriptedHandler : IJobHandler
    {
        public string Kind => JobKinds.Normalise;
        public ConcurrentQueue<Func<Job, Task>> Script { get; } = new();
        public ConcurrentBag<Guid> Invocations { get; } = [];

        public Task HandleAsync(Job job, JobContext context, CancellationToken ct)
        {
            Invocations.Add(job.JobId);
            return Script.TryDequeue(out var step) ? step(job) : Task.CompletedTask;
        }
    }
}
