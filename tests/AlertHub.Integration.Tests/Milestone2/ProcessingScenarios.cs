using System.Text;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Ingest;
using AlertHub.Application.Integrations;
using AlertHub.Application.Processing;
using AlertHub.Domain.Alerts;
using AlertHub.Domain.Common;
using AlertHub.Domain.Episodes;
using AlertHub.Domain.Ops;
using AlertHub.Infrastructure.Persistence;
using AlertHub.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AlertHub.Integration.Tests.Milestone2;

/// <summary>
/// Spec §22 scenarios owned by milestone 2: <i>Duplicate webhook delivery</i>, <i>Two workers process one condition</i>,
/// <i>Recovery arrives before opening</i>, <i>Old recovery during a new episode</i>, <i>Historical replay executes</i>
/// (no-transition part), plus <i>Manual close while source is firing</i> (processing half).
/// Events flow through the real ingest service and the real <see cref="EventProcessor"/> against PostgreSQL.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ProcessingScenarios(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    private string _cs = string.Empty;
    private FakeTimeProvider _time = null!;
    private ServiceProvider _services = null!;
    private IntegrationCredentials _integration = null!;

    public async Task InitializeAsync()
    {
        _cs = await postgres.CreateDatabaseAsync("processing");
        _time = new FakeTimeProvider(T0);
        _services = TestServices.Build(_cs, _time);
        _integration = await TestServices.CreateIntegrationAsync(_services, "generic", "generic_webhook", "scope-a");
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    private Guid IntegrationId => _integration.Integration.IntegrationId;

    private async Task<NormaliseJobPayload> IngestAsync(string body, IReadOnlyDictionary<string, string>? headers = null)
    {
        var accepted = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<IngestService>()
            .AcceptAsync(_integration.Integration, new IngestRequest(Encoding.UTF8.GetBytes(body), "application/json", headers ?? new Dictionary<string, string>(), null)));
        return new NormaliseJobPayload(accepted.EventId, accepted.ReceivedAt, IntegrationId);
    }

    private Task<ProcessingResult> ProcessAsync(NormaliseJobPayload payload, bool replay = false)
        => TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<EventProcessor>().ProcessAsync(payload with { IsReplay = replay }));

    private async Task<ProcessingResult> IngestAndProcessAsync(string body) => await ProcessAsync(await IngestAsync(body));

    private static string Firing(string alertId, string eventId, DateTimeOffset at, string severity = "high", string rule = "5xx", string? resource = null, string? version = null)
        => $$"""{"eventType":"firing","alertId":"{{alertId}}","eventId":"{{eventId}}","occurredAt":"{{at:O}}","severity":"{{severity}}","environment":"production","service":"orders","resource":{"id":"{{resource ?? "res-" + alertId}}","name":"Orders"},"rule":{"id":"{{rule}}","name":"5xx rate"},"summary":"5xx high"{{(version is null ? "" : $",\"version\":\"{version}\"")}}}""";

    private static string Resolved(string alertId, string eventId, DateTimeOffset at, string rule = "5xx", string? resource = null, string? version = null)
        => $$"""{"eventType":"resolved","alertId":"{{alertId}}","eventId":"{{eventId}}","occurredAt":"{{at:O}}","severity":"high","environment":"production","resource":{"id":"{{resource ?? "res-" + alertId}}"},"rule":{"id":"{{rule}}"}{{(version is null ? "" : $",\"version\":\"{version}\"")}}}""";

    private Task<Episode> LoadEpisodeAsync(Guid id) => TestServices.InDbAsync(_services, db => db.Episodes.AsNoTracking().SingleAsync(e => e.EpisodeId == id));
    private Task<List<Episode>> AllEpisodesAsync() => TestServices.InDbAsync(_services, db => db.Episodes.AsNoTracking().OrderBy(e => e.CreatedAt).ToListAsync());
    private Task<List<EpisodeEvent>> TimelineAsync(Guid id) => TestServices.InDbAsync(_services, db => db.EpisodeEvents.AsNoTracking().Where(e => e.EpisodeId == id).OrderBy(e => e.At).ToListAsync());

    [Fact]
    public async Task First_firing_opens_an_episode_with_identity_timeline_and_audit()
    {
        var result = await IngestAndProcessAsync(Firing("a1-t1", "e1-t1", T0));

        result.Outcome.Should().Be(ProcessingOutcome.Opened);
        var episode = await LoadEpisodeAsync(result.EpisodeId!.Value);
        episode.ConditionState.Should().Be(ConditionState.Firing);
        episode.HandlingState.Should().Be(HandlingState.New);
        episode.Severity.Should().Be(Severity.High);
        episode.AccessScope.Should().Be("scope-a");
        episode.IntegrationId.Should().Be(IntegrationId);
        episode.OccurrenceCount.Should().Be(1);
        episode.FirstSeen.Should().Be(T0);
        episode.Summary.Should().Be("5xx high");
        episode.SourceAlertId.Should().Be("a1-t1");

        await using var db = PostgresFixture.CreateContext(_cs);
        (await db.Identities.CountAsync(i => i.Fingerprint == episode.Fingerprint && i.EpisodeCount == 1)).Should().Be(1);
        (await db.AppliedEvents.CountAsync(a => a.IntegrationId == IntegrationId && a.DeliveryKey.StartsWith("evt:e1") && a.Outcome == "applied")).Should().Be(1);
        var normalised = await db.NormalisedEvents.SingleAsync();
        normalised.EpisodeId.Should().Be(episode.EpisodeId);
        normalised.IdentityComponents.Should().NotBeNull();
        (await db.AuditEntries.CountAsync(a => a.Action == "episode.open" && a.TargetId == episode.EpisodeId.ToString())).Should().Be(1);
        (await TimelineAsync(episode.EpisodeId)).Should().ContainSingle(t => t.Kind == EpisodeEventKind.SourceEvent);
    }

    [Fact]
    public async Task Scenario_DuplicateWebhookDelivery_second_identical_body_changes_nothing()
    {
        var body = Firing("a1-t2", "e1-t2", T0);
        var first = await IngestAndProcessAsync(body);
        var second = await IngestAndProcessAsync(body);
        var third = await IngestAndProcessAsync(body);

        first.Outcome.Should().Be(ProcessingOutcome.Opened);
        second.Outcome.Should().Be(ProcessingOutcome.Duplicate);
        third.Outcome.Should().Be(ProcessingOutcome.Duplicate);

        var episode = await LoadEpisodeAsync(first.EpisodeId!.Value);
        episode.OccurrenceCount.Should().Be(1, "a delivery duplicate never counts as an occurrence");
        episode.Version.Should().Be(1, "no transition happened");

        await using var db = PostgresFixture.CreateContext(_cs);
        (await db.AppliedEvents.CountAsync()).Should().Be(1);
        var dup = await db.DeliveryDuplicates.SingleAsync();
        dup.Count.Should().Be(2);
        dup.DeliveryKey.Should().StartWith("evt:e1");
        (await db.RawEvents.CountAsync()).Should().Be(3, "raw bytes are always retained");
        (await db.NormalisedEvents.CountAsync()).Should().Be(1, "duplicates are not stored as normalised events");
        (await TimelineAsync(episode.EpisodeId)).Should().ContainSingle(t => t.Kind == EpisodeEventKind.SourceEvent).And.NotContain(t => t.Kind == EpisodeEventKind.LateEvent);
    }

    [Fact]
    public async Task Repeats_update_last_seen_and_count_and_severity_is_the_maximum()
    {
        var opened = await IngestAndProcessAsync(Firing("a1-t3", "e1-t3", T0, "medium"));
        var updated = await IngestAndProcessAsync(Firing("a1-t3", "e2-t3", T0.AddMinutes(1), "critical"));
        var lower = await IngestAndProcessAsync(Firing("a1-t3", "e3-t3", T0.AddMinutes(2), "low"));

        updated.Outcome.Should().Be(ProcessingOutcome.Updated);
        lower.Outcome.Should().Be(ProcessingOutcome.Updated);
        updated.EpisodeId.Should().Be(opened.EpisodeId!.Value);
        var episode = await LoadEpisodeAsync(opened.EpisodeId!.Value);
        episode.OccurrenceCount.Should().Be(3);
        episode.LastSeen.Should().Be(T0.AddMinutes(2));
        episode.Severity.Should().Be(Severity.Critical);
        (await AllEpisodesAsync()).Should().HaveCount(1);

        await using var db = PostgresFixture.CreateContext(_cs);
        (await db.AuditEntries.CountAsync(a => a.Action == "episode.severity_increase")).Should().Be(1);
    }

    [Fact]
    public async Task Scenario_TwoWorkersProcessOneCondition_concurrent_openings_yield_one_episode_with_both_events_applied()
    {
        var payloads = new List<NormaliseJobPayload>();
        for (var i = 0; i < 8; i++)
        {
            payloads.Add(await IngestAsync(Firing("a1-t4", $"e{i}", T0.AddSeconds(i))));
        }

        var results = await Task.WhenAll(payloads.Select(p => Task.Run(() => ProcessAsync(p))));

        results.Count(r => r.Outcome == ProcessingOutcome.Opened).Should().Be(1);
        results.Count(r => r.Outcome == ProcessingOutcome.Updated).Should().Be(7);
        var episodes = await AllEpisodesAsync();
        episodes.Should().ContainSingle();
        episodes[0].OccurrenceCount.Should().Be(8);
        episodes[0].LastSeen.Should().Be(T0.AddSeconds(7));

        await using var db = PostgresFixture.CreateContext(_cs);
        (await db.AppliedEvents.CountAsync(a => a.Outcome == "applied")).Should().Be(8);
        (await db.NormalisedEvents.CountAsync(n => n.EpisodeId == episodes[0].EpisodeId)).Should().Be(8);
    }

    [Fact]
    public async Task Source_resolved_closes_with_evidence_source_and_a_later_firing_opens_a_linked_new_episode()
    {
        var opened = await IngestAndProcessAsync(Firing("a1-t5", "e1-t5", T0));
        var resolved = await IngestAndProcessAsync(Resolved("a1-t5", "e2-t5", T0.AddMinutes(5)));

        resolved.Outcome.Should().Be(ProcessingOutcome.Resolved);
        var closed = await LoadEpisodeAsync(opened.EpisodeId!.Value);
        closed.ConditionState.Should().Be(ConditionState.Resolved);
        closed.HandlingState.Should().Be(HandlingState.Closed);
        closed.ClosureReason.Should().Be(ClosureReason.SourceResolved);
        closed.ResolutionEvidence.Should().Be(Evidence.Source);

        var again = await IngestAndProcessAsync(Firing("a1-t5", "e3-t5", T0.AddMinutes(10)));
        again.Outcome.Should().Be(ProcessingOutcome.Opened);
        again.EpisodeId.Should().NotBe(opened.EpisodeId!.Value);
        var recurrence = await LoadEpisodeAsync(again.EpisodeId!.Value);
        recurrence.PreviousEpisodeId.Should().Be(opened.EpisodeId!.Value);
        recurrence.HandlingState.Should().Be(HandlingState.New, "previous acknowledgement never carries over");
        recurrence.Fingerprint.Should().Be(closed.Fingerprint);

        await using var db = PostgresFixture.CreateContext(_cs);
        (await db.Identities.SingleAsync()).EpisodeCount.Should().Be(2);
    }

    [Fact]
    public async Task Scenario_RecoveryArrivesBeforeOpening_delayed_opening_does_not_reactivate()
    {
        var recovery = await IngestAndProcessAsync(Resolved("a1-t6", "e-resolved-t6", T0.AddMinutes(5)));
        recovery.Outcome.Should().Be(ProcessingOutcome.RecordedWithoutEpisode);

        var delayedOpening = await IngestAndProcessAsync(Firing("a1-t6", "e-firing-t6", T0));
        delayedOpening.Outcome.Should().Be(ProcessingOutcome.Late);
        (await AllEpisodesAsync()).Should().BeEmpty();

        await using var db = PostgresFixture.CreateContext(_cs);
        var marker = await db.SourceInstanceStates.SingleAsync();
        marker.SourceAlertId.Should().Be("a1-t6");
        marker.ResolvedAt.Should().Be(T0.AddMinutes(5));
        (await db.AppliedEvents.SingleAsync(a => a.DeliveryKey.StartsWith("evt:e-firing"))).Outcome.Should().Be("late");

        // A genuinely new firing after the recovery opens normally.
        var fresh = await IngestAndProcessAsync(Firing("a1-t6", "e-new-t6", T0.AddMinutes(6)));
        fresh.Outcome.Should().Be(ProcessingOutcome.Opened);
    }

    [Fact]
    public async Task Scenario_OldRecoveryDuringNewEpisode_new_episode_stays_active_and_the_event_is_recorded_late()
    {
        var a = await IngestAndProcessAsync(Firing("a1-t7", "e1-t7", T0));
        (await IngestAndProcessAsync(Resolved("a1-t7", "e2-t7", T0.AddMinutes(5)))).Outcome.Should().Be(ProcessingOutcome.Resolved);
        var b = await IngestAndProcessAsync(Firing("a1-t7", "e3-t7", T0.AddMinutes(10)));
        b.Outcome.Should().Be(ProcessingOutcome.Opened);

        // A recovery whose occurred_at falls inside A's window arrives now.
        var oldRecovery = await IngestAndProcessAsync(Resolved("a1-t7", "e4-t7", T0.AddMinutes(3)));

        oldRecovery.Outcome.Should().Be(ProcessingOutcome.Late);
        oldRecovery.EpisodeId.Should().Be(b.EpisodeId!.Value);
        var episodeB = await LoadEpisodeAsync(b.EpisodeId!.Value);
        episodeB.ConditionState.Should().Be(ConditionState.Firing);
        episodeB.HandlingState.Should().Be(HandlingState.New);
        (await TimelineAsync(episodeB.EpisodeId)).Should().ContainSingle(t => t.Kind == EpisodeEventKind.LateEvent);
        (await LoadEpisodeAsync(a.EpisodeId!.Value)).HandlingState.Should().Be(HandlingState.Closed, "A is untouched");
    }

    [Fact]
    public async Task Producer_versions_take_precedence_over_timestamps()
    {
        var opened = await IngestAndProcessAsync(Firing("a1-t8", "e1-t8", T0, version: "5"));
        var stale = await IngestAndProcessAsync(Firing("a1-t8", "e2-t8", T0.AddMinutes(1), "critical", version: "4"));
        var newer = await IngestAndProcessAsync(Resolved("a1-t8", "e3-t8", T0.AddMinutes(-30), version: "6"));

        stale.Outcome.Should().Be(ProcessingOutcome.Late);
        newer.Outcome.Should().Be(ProcessingOutcome.Resolved, "a newer version applies even with an older timestamp");
        var episode = await LoadEpisodeAsync(opened.EpisodeId!.Value);
        episode.Severity.Should().Be(Severity.High, "the stale version did not raise severity");
        episode.LastAppliedVersion.Should().Be("6");
    }

    [Fact]
    public async Task Scenario_HistoricalReplay_executes_without_transitions_or_new_episodes()
    {
        var opened = await IngestAndProcessAsync(Firing("a1-t9", "e1-t9", T0));
        (await IngestAndProcessAsync(Resolved("a1-t9", "e2-t9", T0.AddMinutes(5)))).Outcome.Should().Be(ProcessingOutcome.Resolved);
        var before = await LoadEpisodeAsync(opened.EpisodeId!.Value);

        // Replay both raw events and a never-seen firing in historical mode.
        var replayed1 = await ProcessAsync(await IngestAsync(Firing("a1-t9", "e1-t9", T0)), replay: true);
        var replayed2 = await ProcessAsync(await IngestAsync(Firing("a1-t9", "e-unseen-t9", T0.AddMinutes(20))), replay: true);

        replayed1.Outcome.Should().Be(ProcessingOutcome.Replayed);
        replayed1.EpisodeId.Should().Be(opened.EpisodeId!.Value);
        replayed2.Outcome.Should().Be(ProcessingOutcome.Replayed);
        (await AllEpisodesAsync()).Should().ContainSingle();
        var after = await LoadEpisodeAsync(opened.EpisodeId.Value);
        after.Version.Should().Be(before.Version);
        after.HandlingState.Should().Be(HandlingState.Closed);
        (await TimelineAsync(opened.EpisodeId.Value)).Should().ContainSingle(t => t.Kind == EpisodeEventKind.Replayed);

        await using var db = PostgresFixture.CreateContext(_cs);
        (await db.AppliedEvents.SingleAsync(a => a.DeliveryKey.StartsWith("evt:e-unseen"))).Outcome.Should().Be("replayed");
        (await db.Jobs.CountAsync(j => j.Kind != "normalise" && (j.Status == JobStatus.Pending || j.Status == JobStatus.Reserved || j.Status == JobStatus.Suspended)))
            .Should().Be(0, "replay stages no live timers and no escalations (the opened episode's own timers were cancelled by its closure)");
    }

    [Fact]
    public async Task Scenario_ManualCloseWhileSourceIsFiring_processing_half_new_episode_opens_and_closed_one_keeps_its_condition()
    {
        var opened = await IngestAndProcessAsync(Firing("a1-t10", "e1-t10", T0));
        await TestServices.InDbAsync(_services, async db =>
        {
            var e = await db.Episodes.SingleAsync(x => x.EpisodeId == opened.EpisodeId);
            e.CloseManually("known issue, tracked elsewhere", T0.AddMinutes(1));
            return await db.SaveChangesAsync();
        });

        var stillFiring = await IngestAndProcessAsync(Firing("a1-t10", "e2-t10", T0.AddMinutes(2)));

        stillFiring.Outcome.Should().Be(ProcessingOutcome.Opened);
        var previous = await LoadEpisodeAsync(opened.EpisodeId!.Value);
        previous.ConditionState.Should().Be(ConditionState.Firing);
        previous.ClosureReason.Should().Be(ClosureReason.ManualClose);
        previous.ResolutionEvidence.Should().Be(Evidence.Human);
        var current = await LoadEpisodeAsync(stillFiring.EpisodeId!.Value);
        current.PreviousEpisodeId.Should().Be(previous.EpisodeId);
        current.HandlingState.Should().Be(HandlingState.New);
    }

    [Fact]
    public async Task Cancelled_closes_as_source_cancelled_not_applicable()
    {
        var opened = await IngestAndProcessAsync(Firing("a1-t11", "e1-t11", T0));
        var cancelled = await IngestAndProcessAsync(Firing("a1-t11", "e2-t11", T0.AddMinutes(1)).Replace("\"firing\"", "\"cancelled\"", StringComparison.Ordinal));
        cancelled.Outcome.Should().Be(ProcessingOutcome.Cancelled);
        var episode = await LoadEpisodeAsync(opened.EpisodeId!.Value);
        episode.ConditionState.Should().Be(ConditionState.NotApplicable);
        episode.ClosureReason.Should().Be(ClosureReason.SourceCancelled);
    }

    [Fact]
    public async Task Unmappable_payload_is_quarantined_and_the_job_does_not_fail()
    {
        var garbage = await IngestAndProcessAsync("this is not json");
        var missing = await IngestAndProcessAsync("""{"eventType":"firing"}""");
        var badType = await IngestAndProcessAsync("""{"eventType":"kaboom","alertId":"x","rule":{"id":"r"}}""");

        garbage.Outcome.Should().Be(ProcessingOutcome.MappingFailed);
        missing.Outcome.Should().Be(ProcessingOutcome.MappingFailed);
        badType.Outcome.Should().Be(ProcessingOutcome.MappingFailed);

        await using var db = PostgresFixture.CreateContext(_cs);
        var failures = await db.MappingFailures.OrderBy(f => f.RawReceivedAt).ToListAsync();
        failures.Should().HaveCount(3).And.OnlyContain(f => f.Quarantined && f.IntegrationId == IntegrationId);
        failures[1].Field.Should().Be("source_alert_id");
        failures[2].Field.Should().Be("event_type");
        (await db.Episodes.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Heartbeat_events_never_create_episodes()
    {
        var hb = await IngestAndProcessAsync("""{"eventType":"heartbeat","alertId":"canary-1","eventId":"h1","rule":{"id":"alerthub-canary"}}""");
        hb.Outcome.Should().Be(ProcessingOutcome.CoverageSignal);
        (await AllEpisodesAsync()).Should().BeEmpty();
        await using var db = PostgresFixture.CreateContext(_cs);
        (await db.AppliedEvents.SingleAsync()).Outcome.Should().Be("coverage");
    }

    [Fact]
    public async Task Reprocessing_the_same_job_after_a_crash_is_idempotent()
    {
        var payload = await IngestAsync(Firing("a1-t14", "e1-t14", T0));
        var first = await ProcessAsync(payload);
        var again = await ProcessAsync(payload);
        first.Outcome.Should().Be(ProcessingOutcome.Opened);
        again.Outcome.Should().Be(ProcessingOutcome.Duplicate);
        (await LoadEpisodeAsync(first.EpisodeId!.Value)).OccurrenceCount.Should().Be(1);
    }
}
