using AlertHub.Application.Ops;
using AlertHub.Domain.Ops;
using AlertHub.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AlertHub.Integration.Tests.Milestone1;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class JobQueueTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    private string _cs = string.Empty;
    private FakeTimeProvider _time = null!;
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        _cs = await postgres.CreateDatabaseAsync("queue");
        _time = new FakeTimeProvider(T0);
        _services = TestServices.Build(_cs, _time);
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    private async Task<Job> EnqueueAsync(string kind = "normalise", int maxAttempts = 10, DateTimeOffset? notBefore = null, short priority = 100)
    {
        var job = new Job
        {
            JobId = Guid.CreateVersion7(_time.GetUtcNow()),
            Kind = kind,
            Payload = "{}",
            NotBefore = notBefore ?? _time.GetUtcNow(),
            MaxAttempts = maxAttempts,
            Priority = priority,
            CreatedAt = _time.GetUtcNow(),
            UpdatedAt = _time.GetUtcNow(),
        };
        await TestServices.InScopeAsync(_services, async sp =>
        {
            sp.GetRequiredService<IJobQueue>().Enqueue(job);
            await sp.GetRequiredService<Application.Abstractions.IUnitOfWork>().CommitAsync();
        });
        return job;
    }

    private Task<Job> LoadAsync(Guid id) => TestServices.InDbAsync(_services, db => db.Jobs.AsNoTracking().SingleAsync(j => j.JobId == id));

    [Fact]
    public async Task Claim_reserves_due_jobs_in_priority_then_time_order_and_skips_future_ones()
    {
        var later = await EnqueueAsync(notBefore: T0.AddMinutes(5));
        var low = await EnqueueAsync(priority: 200);
        var high = await EnqueueAsync(priority: 10);
        var normal = await EnqueueAsync();

        var claimed = await TestServices.InQueueAsync(_services, q => q.ClaimAsync(["normalise"], "w1", TimeSpan.FromMinutes(2), 10));

        claimed.Select(j => j.JobId).Should().Equal(high.JobId, normal.JobId, low.JobId);
        claimed.Should().OnlyContain(j => j.Status == JobStatus.Reserved && j.ReservedBy == "w1" && j.Attempts == 1 && j.ReservedUntil == T0.AddMinutes(2));
        (await LoadAsync(later.JobId)).Status.Should().Be(JobStatus.Pending);
    }

    [Fact]
    public async Task Two_workers_claiming_concurrently_never_get_the_same_job()
    {
        for (var i = 0; i < 40; i++) await EnqueueAsync();

        var tasks = Enumerable.Range(0, 4).Select(i => Task.Run(async () =>
        {
            var all = new List<Guid>();
            for (var round = 0; round < 5; round++)
            {
                var batch = await TestServices.InQueueAsync(_services, q => q.ClaimAsync(["normalise"], $"w{i}", TimeSpan.FromMinutes(2), 3));
                all.AddRange(batch.Select(j => j.JobId));
            }
            return all;
        })).ToArray();
        var results = await Task.WhenAll(tasks);

        var claimed = results.SelectMany(r => r).ToList();
        claimed.Should().HaveCount(40);
        claimed.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Complete_requires_the_reservation_token()
    {
        var job = await EnqueueAsync();
        await TestServices.InQueueAsync(_services, q => q.ClaimAsync(["normalise"], "owner", TimeSpan.FromMinutes(2), 1));

        (await TestServices.InQueueAsync(_services, q => q.CompleteAsync(job.JobId, "impostor"))).Should().BeFalse();
        (await LoadAsync(job.JobId)).Status.Should().Be(JobStatus.Reserved);

        (await TestServices.InQueueAsync(_services, q => q.CompleteAsync(job.JobId, "owner"))).Should().BeTrue();
        var done = await LoadAsync(job.JobId);
        done.Status.Should().Be(JobStatus.Done);
        done.ReservedBy.Should().BeNull();
        (await TestServices.InQueueAsync(_services, q => q.CompleteAsync(job.JobId, "owner"))).Should().BeFalse("already done");
    }

    [Fact]
    public async Task Complete_after_the_lease_expired_is_rejected()
    {
        var job = await EnqueueAsync();
        await TestServices.InQueueAsync(_services, q => q.ClaimAsync(["normalise"], "owner", TimeSpan.FromMinutes(2), 1));
        _time.Advance(TimeSpan.FromMinutes(3));
        (await TestServices.InQueueAsync(_services, q => q.CompleteAsync(job.JobId, "owner"))).Should().BeFalse("the lease expired");
        (await LoadAsync(job.JobId)).Status.Should().Be(JobStatus.Reserved, "only the reaper changes an expired reservation");
    }

    [Fact]
    public async Task Expired_reservations_are_reaped_back_to_pending_and_reclaimable()
    {
        var job = await EnqueueAsync();
        await TestServices.InQueueAsync(_services, q => q.ClaimAsync(["normalise"], "dead-worker", TimeSpan.FromMinutes(2), 1));

        (await TestServices.InQueueAsync(_services, q => q.ReapExpiredAsync())).Should().Be(0, "the lease is still valid");
        _time.Advance(TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(1)));
        (await TestServices.InQueueAsync(_services, q => q.ReapExpiredAsync())).Should().Be(1);

        var reaped = await LoadAsync(job.JobId);
        reaped.Status.Should().Be(JobStatus.Pending);
        reaped.ReservedBy.Should().BeNull();
        reaped.LastError.Should().Contain("dead-worker");

        var again = await TestServices.InQueueAsync(_services, q => q.ClaimAsync(["normalise"], "w2", TimeSpan.FromMinutes(2), 1));
        again.Should().ContainSingle(j => j.JobId == job.JobId && j.Attempts == 2);
    }

    [Fact]
    public async Task Fail_reschedules_with_backoff_until_attempts_are_exhausted_then_moves_to_failure_queue()
    {
        var job = await EnqueueAsync(maxAttempts: 2);

        await TestServices.InQueueAsync(_services, q => q.ClaimAsync(["normalise"], "w1", TimeSpan.FromMinutes(2), 1));
        var first = await TestServices.InQueueAsync(_services, q => q.FailAsync(job.JobId, "w1", "boom 1"));
        first.Should().Be(JobFailureOutcome.Rescheduled);
        var afterFirst = await LoadAsync(job.JobId);
        afterFirst.Status.Should().Be(JobStatus.Pending);
        afterFirst.NotBefore.Should().BeAfter(T0).And.BeOnOrBefore(T0.Add(Backoff.Base));
        afterFirst.LastError.Should().Be("boom 1");

        (await TestServices.InQueueAsync(_services, q => q.ClaimAsync(["normalise"], "w1", TimeSpan.FromMinutes(2), 1))).Should().BeEmpty("backoff not elapsed");
        _time.Advance(Backoff.Base);
        (await TestServices.InQueueAsync(_services, q => q.ClaimAsync(["normalise"], "w1", TimeSpan.FromMinutes(2), 1))).Should().ContainSingle();

        var second = await TestServices.InQueueAsync(_services, q => q.FailAsync(job.JobId, "w1", "boom 2"));
        second.Should().Be(JobFailureOutcome.MovedToFailureQueue);
        var failed = await LoadAsync(job.JobId);
        failed.Status.Should().Be(JobStatus.Failed);
        failed.Attempts.Should().Be(2);

        (await TestServices.InQueueAsync(_services, q => q.RetryFailedAsync(job.JobId))).Should().BeTrue();
        var retried = await LoadAsync(job.JobId);
        retried.Status.Should().Be(JobStatus.Pending);
        retried.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task Fail_by_a_worker_that_lost_its_reservation_records_nothing()
    {
        var job = await EnqueueAsync();
        await TestServices.InQueueAsync(_services, q => q.ClaimAsync(["normalise"], "w1", TimeSpan.FromMinutes(2), 1));
        _time.Advance(TimeSpan.FromMinutes(3));
        await TestServices.InQueueAsync(_services, q => q.ReapExpiredAsync());
        await TestServices.InQueueAsync(_services, q => q.ClaimAsync(["normalise"], "w2", TimeSpan.FromMinutes(2), 1));

        (await TestServices.InQueueAsync(_services, q => q.FailAsync(job.JobId, "w1", "stale"))).Should().Be(JobFailureOutcome.ReservationLost);
        var current = await LoadAsync(job.JobId);
        current.ReservedBy.Should().Be("w2");
        current.LastError.Should().NotBe("stale");
    }

    [Fact]
    public async Task Extend_pushes_the_lease_only_for_the_owner()
    {
        var job = await EnqueueAsync();
        await TestServices.InQueueAsync(_services, q => q.ClaimAsync(["normalise"], "w1", TimeSpan.FromMinutes(2), 1));
        (await TestServices.InQueueAsync(_services, q => q.ExtendAsync(job.JobId, "w2", TimeSpan.FromMinutes(10)))).Should().BeFalse();
        (await TestServices.InQueueAsync(_services, q => q.ExtendAsync(job.JobId, "w1", TimeSpan.FromMinutes(10)))).Should().BeTrue();
        (await LoadAsync(job.JobId)).ReservedUntil.Should().Be(T0.AddMinutes(10));
    }
}
