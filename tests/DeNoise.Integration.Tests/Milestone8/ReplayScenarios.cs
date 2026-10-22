using System.Text;
using System.Text.Json;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Ingest;
using DeNoise.Application.Integrations;
using DeNoise.Application.Mapping;
using DeNoise.Application.Ops;
using DeNoise.Application.Processing;
using DeNoise.Application.Replay;
using DeNoise.Domain.Episodes;
using DeNoise.Domain.Ops;
using DeNoise.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DeNoise.Integration.Tests.Milestone8;

/// <summary>
/// Spec §17.5 replay: quarantined events are retried live after a mapping fix (and leave the failure queue), historical replay
/// re-records without transitions, and every job leaves selection, actor and outcome behind (audit + <c>ops.job.result</c>).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ReplayScenarios(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    private string _cs = string.Empty;
    private FakeTimeProvider _time = null!;
    private ServiceProvider _services = null!;
    private IntegrationCredentials _integration = null!;

    private const string FixedMappingYaml = """
        mapping:
          fields:
            event_type: { lookup: { from: "$.state", map: { on: firing, off: resolved }, default: unknown } }
            source_alert_id: { path: "$.id" }
            source_event_id: { path: "$.seq" }
            rule_id: { path: "$.rule" }
            severity: { const: high }
            summary: { template: "legacy {$.rule} on {$.id}" }
          required: [event_type, source_alert_id, rule_id]
          identity: [ { name: rule, field: rule_id }, { name: id, field: source_alert_id } ]
        """;

    public async Task InitializeAsync()
    {
        _cs = await postgres.CreateDatabaseAsync("replay");
        _time = new FakeTimeProvider(T0);
        _services = TestServices.Build(_cs, _time);
        _integration = await TestServices.CreateIntegrationAsync(_services, "legacy", "generic_webhook", "scope-a");
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    private Guid IntegrationId => _integration.Integration.IntegrationId;

    private async Task<NormaliseJobPayload> IngestAsync(string body)
    {
        var accepted = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<IngestService>()
            .AcceptAsync(_integration.Integration, new IngestRequest(Encoding.UTF8.GetBytes(body), "application/json", new Dictionary<string, string>(), null)));
        return new NormaliseJobPayload(accepted.EventId, accepted.ReceivedAt, IntegrationId);
    }

    private Task<ProcessingResult> ProcessAsync(NormaliseJobPayload payload)
        => TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<EventProcessor>().ProcessAsync(payload));

    /// <summary>Claims the replay job like the processing runner would and runs the handler; returns the finished job row.</summary>
    private async Task<Job> RunReplayAsync(Guid jobId)
    {
        var claimed = await TestServices.InQueueAsync(_services, q => q.ClaimAsync(JobKinds.Processing, "worker-r", TimeSpan.FromMinutes(2), 10));
        var job = claimed.Should().ContainSingle(j => j.JobId == jobId).Subject;
        await TestServices.InScopeAsync(_services, async sp =>
        {
            var handler = sp.GetServices<IJobHandler>().Single(h => h.Kind == JobKinds.Replay);
            var context = new JobContext("worker-r", (_, _) => Task.FromResult(true));
            await handler.HandleAsync(job, context, CancellationToken.None);
            (await sp.GetRequiredService<IJobQueue>().CompleteAsync(job.JobId, "worker-r")).Should().BeTrue();
        });
        return (await TestServices.InQueueAsync(_services, q => q.GetAsync(jobId)))!;
    }

    [Fact]
    public async Task Scenario_RetryFailed_replays_quarantined_events_through_the_fixed_mapping_and_clears_the_queue()
    {
        // Legacy producer without eventType: the generic mapping quarantines both events.
        var bad1 = await IngestAsync("""{"state":"on","id":"db-1","seq":"1","rule":"disk"}""");
        var bad2 = await IngestAsync("""{"state":"on","id":"db-2","seq":"2","rule":"disk"}""");
        (await ProcessAsync(bad1)).Outcome.Should().Be(ProcessingOutcome.MappingFailed);
        (await ProcessAsync(bad2)).Outcome.Should().Be(ProcessingOutcome.MappingFailed);
        await using (var db = PostgresFixture.CreateContext(_cs))
        {
            (await db.MappingFailures.CountAsync(f => f.Quarantined)).Should().Be(2);
        }

        // The operator ships a mapping for the legacy shape and activates it.
        var actor = Actor.System("integration-admin");
        var version = await TestServices.InScopeAsync(_services, async sp =>
        {
            var service = sp.GetRequiredService<MappingService>();
            var v = await service.CreateVersionAsync(new CreateMappingVersion(IntegrationId, FixedMappingYaml, Name: "legacy"), actor);
            await service.ActivateAsync(v.MappingId, v.Version, actor);
            return v;
        });

        // Retry only the first failure; the second stays quarantined.
        var job = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<ReplayService>()
            .StartAsync(new StartReplay(ReplayModes.RetryFailed, IntegrationId, [bad1.EventId], MappingVersion: version.Version), actor));
        job.Kind.Should().Be(JobKinds.Replay);
        var finished = await RunReplayAsync(job.JobId);
        finished.Status.Should().Be(JobStatus.Done);
        var result = JsonSerializer.Deserialize<ReplayResult>(finished.Result!, JsonDefaults.Stored)!;
        result.Selected.Should().Be(1);
        result.Outcomes.Should().ContainKey(nameof(ProcessingOutcome.Opened)).WhoseValue.Should().Be(1);
        result.FailuresResolved.Should().Be(1);

        await using (var db = PostgresFixture.CreateContext(_cs))
        {
            var episode = await db.Episodes.AsNoTracking().SingleAsync();
            episode.Summary.Should().Be("legacy disk on db-1");
            episode.HandlingState.Should().Be(HandlingState.New, "a retried event is live: it opens, routes and notifies like any first application");
            (await db.MappingFailures.SingleAsync(f => f.EventId == bad1.EventId)).Quarantined.Should().BeFalse();
            (await db.MappingFailures.SingleAsync(f => f.EventId == bad2.EventId)).Quarantined.Should().BeTrue("not selected");
            (await db.AuditEntries.CountAsync(a => a.Action == "replay.start")).Should().Be(1);
        }

        // Retry everything that is still quarantined.
        var all = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<ReplayService>().StartAsync(new StartReplay(ReplayModes.RetryFailed, IntegrationId), actor));
        var allDone = await RunReplayAsync(all.JobId);
        JsonSerializer.Deserialize<ReplayResult>(allDone.Result!, JsonDefaults.Stored)!.Processed.Should().Be(1);
        await using (var db = PostgresFixture.CreateContext(_cs))
        {
            (await db.MappingFailures.CountAsync(f => f.Quarantined)).Should().Be(0);
            (await db.Episodes.CountAsync()).Should().Be(2);
        }
    }

    [Fact]
    public async Task Scenario_HistoricalReplay_executes_without_transitions_or_notifications_and_reports()
    {
        var first = await IngestAsync("""{"eventType":"firing","alertId":"h-1","eventId":"e1","severity":"high","rule":{"id":"cpu"},"resource":{"id":"vm1"},"summary":"cpu"}""");
        (await ProcessAsync(first)).Outcome.Should().Be(ProcessingOutcome.Opened);
        _time.Advance(TimeSpan.FromMinutes(5));
        var second = await IngestAsync("""{"eventType":"resolved","alertId":"h-1","eventId":"e2","severity":"high","rule":{"id":"cpu"},"resource":{"id":"vm1"}}""");
        (await ProcessAsync(second)).Outcome.Should().Be(ProcessingOutcome.Resolved);
        int outboxBefore, episodeEventsBefore;
        await using (var db = PostgresFixture.CreateContext(_cs))
        {
            outboxBefore = await db.Outbox.CountAsync();
            episodeEventsBefore = await db.EpisodeEvents.CountAsync();
        }

        var actor = Actor.System("platform-admin");
        var job = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<ReplayService>()
            .StartAsync(new StartReplay(ReplayModes.Historical, IntegrationId, From: T0 - TimeSpan.FromHours(1), To: _time.GetUtcNow() + TimeSpan.FromMinutes(1)), actor));
        var finished = await RunReplayAsync(job.JobId);
        var result = JsonSerializer.Deserialize<ReplayResult>(finished.Result!, JsonDefaults.Stored)!;
        result.Selected.Should().Be(2);
        result.Outcomes.Should().Equal(new Dictionary<string, int> { [nameof(ProcessingOutcome.Replayed)] = 2 });

        await using (var db = PostgresFixture.CreateContext(_cs))
        {
            var episode = await db.Episodes.AsNoTracking().SingleAsync();
            episode.HandlingState.Should().Be(HandlingState.Closed, "historical replay never reopens");
            (await db.Outbox.CountAsync()).Should().Be(outboxBefore, "no historical notifications");
            (await db.EpisodeEvents.CountAsync(e => e.Kind == EpisodeEventKind.Replayed)).Should().Be(2);
            (await db.EpisodeEvents.CountAsync()).Should().Be(episodeEventsBefore + 2);
        }

        // Guard rails: the window and the mode are validated before a job exists.
        var tooWide = () => TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<ReplayService>().StartAsync(new StartReplay(ReplayModes.Historical, IntegrationId, From: T0 - TimeSpan.FromDays(60), To: T0), actor));
        await tooWide.Should().ThrowAsync<ArgumentException>();
        var preview = () => TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<ReplayService>().StartAsync(new StartReplay("preview", IntegrationId), actor));
        await preview.Should().ThrowAsync<ArgumentException>();
    }
}
