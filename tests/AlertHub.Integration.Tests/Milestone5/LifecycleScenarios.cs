using System.Text;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Coverage;
using AlertHub.Application.Ingest;
using AlertHub.Application.Integrations;
using AlertHub.Application.Lifecycle;
using AlertHub.Application.Notifications;
using AlertHub.Application.Ops;
using AlertHub.Application.Policies;
using AlertHub.Application.Processing;
using AlertHub.Application.Teams;
using AlertHub.Domain.Episodes;
using AlertHub.Domain.Notifications;
using AlertHub.Domain.Ops;
using AlertHub.Domain.Policies;
using AlertHub.Infrastructure.Observability;
using AlertHub.Infrastructure.Ops;
using AlertHub.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AlertHub.Integration.Tests.Milestone5;

/// <summary>
/// Spec §22 lifecycle and coverage scenarios: <i>Repeating source becomes quiet (coverage healthy)</i>, <i>Source quiet, coverage never
/// configured</i>, <i>Azure resolved lost in transit</i>, <i>Operator changes an auto-resolve timeout</i>, <i>Canary rule stops firing</i>,
/// <i>Coverage alert flaps</i>, <i>Ingestion or mapping unhealthy</i>, <i>Old alert reaches administrative expiry</i>, <i>New signal races with
/// expiry</i>, <i>Source resumes after expiry</i>. Real processing, real timers, real database; time is a <see cref="FakeTimeProvider"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class LifecycleScenarios(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    private const string CoverageJson = """{"coverage":{"methods":[{"type":"managed_canary","expected_interval":"5m","delayed_after":"10m","alert_after":"15m","unavailable_after":"30m","recovery_successes_required":3}]}}""";

    private string _cs = string.Empty;
    private FakeTimeProvider _time = null!;
    private ServiceProvider _services = null!;
    private IntegrationCredentials _covered = null!;   // canary-covered integration (scope-a)
    private IntegrationCredentials _uncovered = null!; // no coverage method configured (scope-a)
    private Guid _team;
    private Guid _dest;
    private Guid _repeating;
    private Guid _legacy;

    public async Task InitializeAsync()
    {
        _cs = await postgres.CreateDatabaseAsync("lifecycle");
        _time = new FakeTimeProvider(T0);
        _services = TestServices.Build(_cs, _time, settings: new Dictionary<string, string?> { ["Notifications:AllowInsecureDestinations"] = "true", ["Notifications:PublicBaseUrl"] = "https://hub.test" });
        var actor = Actor.System("setup");
        await TestServices.InScopeAsync(_services, async sp =>
        {
            var teams = sp.GetRequiredService<TeamService>();
            var team = await teams.CreateAsync(new CreateTeam("mpt", ["scope-a"]), actor);
            await teams.CreateAsync(new CreateTeam("triage", ["scope-a"], IsTriage: true), actor);
            _team = team.TeamId;
            var destinations = sp.GetRequiredService<DestinationService>();
            var (primary, _) = await destinations.CreatePairAsync(
                new CreateDestination("mpt-hook", ChannelTypes.Webhook, _team, null, Url: "http://receiver.test/mpt"),
                new CreateDestination("mpt-fallback", ChannelTypes.Webhook, _team, null, Url: "http://receiver.test/mpt-fallback"), actor);
            _dest = primary.Destination.DestinationId;

            var integrations = sp.GetRequiredService<IntegrationService>();
            _covered = await integrations.CreateAsync(new CreateIntegration("canary-src", "generic_webhook", "scope-a", _team, Coverage: CoverageJson), actor);
            _uncovered = await integrations.CreateAsync(new CreateIntegration("plain-src", "generic_webhook", "scope-a", _team), actor);

            var policies = sp.GetRequiredService<PolicyService>();
            _repeating = await Lifecycle(policies, actor, "repeating", """
                lifecycle: { profile: repeating_while_active, expected_repeat_interval: 1m, delivery_grace: 5m }
                auto_resolve: { enabled: true, on_coverage_unknown: close_unverified, on_coverage_degraded: suspend, escalate_before_close_if_severity: [critical, high] }
                """);
            var azure = await Lifecycle(policies, actor, "azure-backstop", "lifecycle: { profile: explicit_recovery }");
            _legacy = await Lifecycle(policies, actor, "legacy", """
                lifecycle: { profile: unknown }
                auto_resolve: { enabled: false }
                expiry: { unknown_lifecycle_review_after: 24h, unverified_expire_after: 7d }
                """);
            var race = await Lifecycle(policies, actor, "race", """
                lifecycle: { profile: unknown }
                auto_resolve: { enabled: false }
                expiry: { unknown_lifecycle_review_after: none, unverified_expire_after: 1h }
                """);

            var routing = await policies.CreateVersionAsync(new CreatePolicyVersion(PolicyKinds.Routing, $$"""
                routing:
                  rules:
                    - { name: repeat, priority: 10, match: { eq: [service, repeat] }, team: {{_team}}, lifecycle_policy: {{_repeating}} }
                    - { name: azure,  priority: 20, match: { eq: [service, azure] },  team: {{_team}}, lifecycle_policy: {{azure}} }
                    - { name: legacy, priority: 30, match: { eq: [service, legacy] }, team: {{_team}}, lifecycle_policy: {{_legacy}} }
                    - { name: race,   priority: 40, match: { eq: [service, race] },   team: {{_team}}, lifecycle_policy: {{race}} }
                """), actor);
            await policies.ActivateAsync(PolicyKinds.Routing, routing.PolicyId, 1, actor);
        });
    }

    private static async Task<Guid> Lifecycle(PolicyService policies, Actor actor, string name, string yaml)
    {
        var v = await policies.CreateVersionAsync(new CreatePolicyVersion(PolicyKinds.Lifecycle, yaml, Name: name), actor);
        await policies.ActivateAsync(PolicyKinds.Lifecycle, v.PolicyId, v.Version, actor);
        return v.PolicyId;
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static string Firing(string alertId, string service, string severity = "medium", DateTimeOffset? at = null, string? eventId = null)
        => $$"""{"eventType":"firing","alertId":"{{alertId}}","eventId":"{{eventId ?? Guid.NewGuid().ToString()}}","occurredAt":"{{(at ?? default):O}}","severity":"{{severity}}","environment":"production","service":"{{service}}","resource":{"id":"res-{{alertId}}","name":"Orders"},"rule":{"id":"r-{{service}}","name":"{{service}} rule"},"summary":"{{service}} alert {{alertId}}"}""";

    private string Heartbeat(bool fail = false)
        => $$"""{"eventType":"heartbeat","alertId":"canary","eventId":"{{Guid.NewGuid()}}","occurredAt":"{{_time.GetUtcNow():O}}","severity":"low","summary":"canary"{{(fail ? ",\"labels\":{\"canary\":\"fail\"}" : "")}}}""";

    private async Task<ProcessingResult> ProcessAsync(IntegrationCredentials integration, string body)
    {
        var accepted = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<IngestService>()
            .AcceptAsync(integration.Integration, new IngestRequest(Encoding.UTF8.GetBytes(body), "application/json", new Dictionary<string, string>(), null)));
        var result = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<EventProcessor>()
            .ProcessAsync(new NormaliseJobPayload(accepted.EventId, accepted.ReceivedAt, integration.Integration.IntegrationId)));
        // Processing ran in-line, so the queued normalise job is consumed here as the processing worker would have done.
        await TestServices.InDbAsync(_services, db => db.Jobs.Where(j => j.Kind == JobKinds.Normalise && j.Status == JobStatus.Pending && j.IntegrationId == integration.Integration.IntegrationId)
            .ExecuteUpdateAsync(u => u.SetProperty(j => j.Status, JobStatus.Done)));
        return result;
    }

    private async Task<Episode> OpenAsync(IntegrationCredentials integration, string alertId, string service, string severity = "medium")
    {
        var body = Firing(alertId, service, severity, _time.GetUtcNow());
        var result = await ProcessAsync(integration, body);
        result.Outcome.Should().Be(ProcessingOutcome.Opened, result.Detail);
        return await EpisodeAsync(result.EpisodeId!.Value);
    }

    private Task<Episode> EpisodeAsync(Guid id) => TestServices.InDbAsync(_services, db => db.Episodes.AsNoTracking().SingleAsync(e => e.EpisodeId == id));

    private Task<List<Job>> JobsAsync(Guid episodeId, string? kind = null) => TestServices.InDbAsync(_services,
        db => db.Jobs.AsNoTracking().Where(j => j.EpisodeId == episodeId && (kind == null || j.Kind == kind)).OrderBy(j => j.CreatedAt).ToListAsync());

    private Task<List<EpisodeEvent>> TimelineAsync(Guid episodeId) => TestServices.InDbAsync(_services,
        db => db.EpisodeEvents.AsNoTracking().Where(e => e.EpisodeId == episodeId).OrderBy(e => e.At).ThenBy(e => e.Id).ToListAsync());

    private Task<List<OutboxMessage>> OutboxAsync(Guid episodeId) => TestServices.InDbAsync(_services,
        db => db.Outbox.AsNoTracking().Where(o => o.EpisodeId == episodeId).OrderBy(o => o.CreatedAt).ToListAsync());

    private JobRunner Scheduler() => new(_services.GetRequiredService<IServiceScopeFactory>(), _services.GetRequiredService<IOptions<JobQueueOptions>>(),
        _time, _services.GetRequiredService<AlertHubMetrics>(), _services.GetRequiredService<ILogger<JobRunner>>(), JobKinds.Scheduler, "scheduler");

    private Task<IReadOnlyList<CoverageEvaluation>> TickCoverageAsync() => TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<CoverageEvaluator>().TickAllAsync(CancellationToken.None));

    private Task<CoverageState?> CoverageAsync(Guid integrationId) => TestServices.InDbAsync(_services, db => db.CoverageStates.AsNoTracking().SingleOrDefaultAsync(c => c.IntegrationId == integrationId));

    private Task<List<Episode>> CoverageEpisodesAsync() => TestServices.InDbAsync(_services, db => db.Episodes.AsNoTracking().Where(e => e.LifecycleProfile == LifecycleProfiles.Coverage).ToListAsync());

    // ---- scenarios -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Scenario_RepeatingSourceBecomesQuiet_coverage_healthy_closes_as_inferred_recovery_with_verified_coverage()
    {
        (await ProcessAsync(_covered, Heartbeat())).Outcome.Should().Be(ProcessingOutcome.CoverageSignal);
        (await CoverageAsync(_covered.Integration.IntegrationId))!.State.Should().Be(CoverageStates.Healthy);

        _time.Advance(TimeSpan.FromMinutes(1));
        var opened = await OpenAsync(_covered, "q1", "repeat");
        opened.LifecycleProfile.Should().Be(LifecycleProfiles.RepeatingWhileActive);
        opened.LifecyclePolicyId.Should().Be(_repeating);
        opened.AutoResolveAt.Should().Be(opened.LastSeen.AddMinutes(15), "max(3 × 1m + 5m, 15m)");
        (await JobsAsync(opened.EpisodeId, JobKinds.AutoResolve)).Should().ContainSingle(j => j.Status == JobStatus.Pending && j.NotBefore == opened.AutoResolveAt);

        // The canary keeps arriving; the alert does not.
        var runner = Scheduler();
        for (var i = 0; i < 3; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(5));
            await ProcessAsync(_covered, Heartbeat());
            await TickCoverageAsync();
            if (i < 2) (await runner.RunBatchAsync(CancellationToken.None)).Should().Be(0, "not due yet");
        }
        // now = T0 + 16m; the deadline (T0 + 1m + 15m) has passed
        (await runner.RunBatchAsync(CancellationToken.None)).Should().Be(1);

        var closed = await EpisodeAsync(opened.EpisodeId);
        closed.HandlingState.Should().Be(HandlingState.Closed);
        closed.ClosureReason.Should().Be(ClosureReason.InactivityTimeout);
        closed.ResolutionEvidence.Should().Be(Evidence.HeartbeatAndInactivity, "coverage was healthy throughout the silence window");
        closed.ConditionState.Should().Be(ConditionState.Resolved);
        (await TimelineAsync(opened.EpisodeId)).Should().Contain(e => e.Kind == EpisodeEventKind.AutoResolve);
        (await OutboxAsync(opened.EpisodeId)).Select(o => o.Type).Should().Contain(NotificationTypes.EpisodeOpened).And.Contain(NotificationTypes.EpisodeClosed);
        (await JobsAsync(opened.EpisodeId)).Should().OnlyContain(j => j.Status == JobStatus.Done || j.Status == JobStatus.Cancelled, "closure cancels the remaining timers");
    }

    [Fact]
    public async Task Scenario_SourceQuiet_CoverageNeverConfigured_still_closes_but_labelled_unverified()
    {
        var opened = await OpenAsync(_uncovered, "q2", "repeat");
        _time.Advance(TimeSpan.FromMinutes(16));
        (await Scheduler().RunBatchAsync(CancellationToken.None)).Should().Be(1);

        var closed = await EpisodeAsync(opened.EpisodeId);
        closed.HandlingState.Should().Be(HandlingState.Closed);
        closed.ClosureReason.Should().Be(ClosureReason.InactivityTimeout);
        closed.ResolutionEvidence.Should().Be(Evidence.InactivityUnverified, "coverage was never established (spec §12.4)");
        var detail = (await TimelineAsync(opened.EpisodeId)).Single(e => e.Kind == EpisodeEventKind.AutoResolve).Detail!;
        detail.Should().Contain("inactivity_unverified").And.MatchRegex("\"coverage\":\\s*\"unknown\"");
    }

    [Fact]
    public async Task Scenario_AzureResolvedLostInTransit_backstop_closes_after_stale_escalation_and_never_claims_source_recovery()
    {
        var opened = await OpenAsync(_uncovered, "q3", "azure", severity: "high");
        opened.LifecycleProfile.Should().Be(LifecycleProfiles.ExplicitRecovery);
        opened.AutoResolveAt.Should().Be(opened.LastSeen.AddMinutes(60), "explicit_recovery preset: 60 min backstop");

        var runner = Scheduler();
        _time.Advance(TimeSpan.FromMinutes(59));
        (await runner.RunBatchAsync(CancellationToken.None)).Should().Be(0);

        // Guard 8: high severity gets a stale-state escalation to the owning team first, then a 30 min lead.
        _time.Advance(TimeSpan.FromMinutes(2));
        (await runner.RunBatchAsync(CancellationToken.None)).Should().Be(1);
        var warned = await EpisodeAsync(opened.EpisodeId);
        warned.HandlingState.Should().Be(HandlingState.New, "not closed yet");
        warned.AutoResolveAt.Should().Be(_time.GetUtcNow().AddMinutes(30));
        (await OutboxAsync(opened.EpisodeId)).Should().Contain(o => o.Type == NotificationTypes.EpisodeStaleCritical && o.DestinationId == _dest);

        _time.Advance(TimeSpan.FromMinutes(31));
        (await runner.RunBatchAsync(CancellationToken.None)).Should().Be(1);
        var closed = await EpisodeAsync(opened.EpisodeId);
        closed.HandlingState.Should().Be(HandlingState.Closed);
        closed.ClosureReason.Should().Be(ClosureReason.InactivityTimeout);
        closed.ResolutionEvidence.Should().NotBe(Evidence.Source, "a backstop closure is never presented as source-confirmed");
        closed.ResolutionEvidence.Should().Be(Evidence.InactivityUnverified);
    }

    [Fact]
    public async Task Scenario_OperatorChangesAutoResolveTimeout_preview_counts_open_episodes_and_activation_reschedules_without_closing()
    {
        var opened = await OpenAsync(_uncovered, "q4", "repeat");
        _time.Advance(TimeSpan.FromMinutes(10));
        var actor = Actor.System("operator");

        var (impactV2, v2) = await TestServices.InScopeAsync(_services, async sp =>
        {
            var policies = sp.GetRequiredService<PolicyService>();
            var version = await policies.CreateVersionAsync(new CreatePolicyVersion(PolicyKinds.Lifecycle, "lifecycle: { profile: repeating_while_active }\nauto_resolve: { after_silence: 45m }", _repeating, "repeating"), actor);
            return (await sp.GetRequiredService<PolicyImpactService>().PreviewAsync(PolicyKinds.Lifecycle, _repeating, version.Version), version.Version);
        });
        impactV2.AffectedOpenEpisodes.Should().Be(1);
        impactV2.Sample.Should().ContainSingle().Which.ProposedAutoResolveAt.Should().Be(opened.LastSeen.AddMinutes(45));
        (await EpisodeAsync(opened.EpisodeId)).AutoResolveAt.Should().Be(opened.LastSeen.AddMinutes(15), "preview changes nothing");

        await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<PolicyService>().ActivateAsync(PolicyKinds.Lifecycle, _repeating, v2, actor));
        var rescheduled = await EpisodeAsync(opened.EpisodeId);
        rescheduled.AutoResolveAt.Should().Be(opened.LastSeen.AddMinutes(45));
        rescheduled.LifecyclePolicyVersion.Should().Be(v2);
        (await JobsAsync(opened.EpisodeId, JobKinds.AutoResolve)).Should().ContainSingle(j => j.Status == JobStatus.Pending).Which.NotBefore.Should().Be(opened.LastSeen.AddMinutes(45));

        // Shortening below the elapsed silence must not close the backlog at activation: the earliest new deadline is a minute out.
        var v3 = await TestServices.InScopeAsync(_services, async sp =>
        {
            var policies = sp.GetRequiredService<PolicyService>();
            var version = await policies.CreateVersionAsync(new CreatePolicyVersion(PolicyKinds.Lifecycle, "lifecycle: { profile: repeating_while_active }\nauto_resolve: { after_silence: 1m }", _repeating, "repeating"), actor);
            await policies.ActivateAsync(PolicyKinds.Lifecycle, _repeating, version.Version, actor);
            return version.Version;
        });
        var shortened = await EpisodeAsync(opened.EpisodeId);
        shortened.HandlingState.Should().Be(HandlingState.New);
        shortened.AutoResolveAt.Should().Be(_time.GetUtcNow().AddMinutes(1));
        shortened.LifecyclePolicyVersion.Should().Be(v3);
        var runner = Scheduler();
        (await runner.RunBatchAsync(CancellationToken.None)).Should().Be(0, "nothing closes instantly");
        _time.Advance(TimeSpan.FromMinutes(2));
        (await runner.RunBatchAsync(CancellationToken.None)).Should().Be(1);
        (await EpisodeAsync(opened.EpisodeId)).HandlingState.Should().Be(HandlingState.Closed);
    }

    [Fact]
    public async Task Scenario_CanaryRuleStopsFiring_coverage_episode_suspends_closure_and_recovery_after_three_successes_resumes_it()
    {
        await ProcessAsync(_covered, Heartbeat());
        _time.Advance(TimeSpan.FromMinutes(1));
        var opened = await OpenAsync(_covered, "q5", "repeat");

        // The canary goes silent past alert_after (15 min).
        _time.Advance(TimeSpan.FromMinutes(15));
        var changed = await TickCoverageAsync();
        changed.Should().ContainSingle(c => c.IntegrationId == _covered.Integration.IntegrationId && c.Step.To == CoverageStates.Degraded);

        var coverage = (await CoverageAsync(_covered.Integration.IntegrationId))!;
        coverage.State.Should().Be(CoverageStates.Degraded);
        var coverageEpisodes = await CoverageEpisodesAsync();
        coverageEpisodes.Should().ContainSingle();
        var coverageEpisode = coverageEpisodes[0];
        coverageEpisode.EpisodeId.Should().Be(coverage.CoverageEpisodeId!.Value);
        coverageEpisode.Severity.Should().Be(Domain.Common.Severity.High);
        coverageEpisode.OwningTeamId.Should().Be(_team, "owner = integration owner team");
        coverageEpisode.Summary.Should().Contain("Monitoring coverage lost for canary-src").And.Contain("suspended for 1 episode");
        (await OutboxAsync(coverageEpisode.EpisodeId)).Should().Contain(o => o.Type == NotificationTypes.CoverageLost && o.DestinationId == _dest);

        var suspended = await EpisodeAsync(opened.EpisodeId);
        suspended.ConditionState.Should().Be(ConditionState.Unknown, "silence now has a competing explanation");
        (await JobsAsync(opened.EpisodeId, JobKinds.AutoResolve)).Should().ContainSingle().Which.Status.Should().Be(JobStatus.Suspended);

        // Its deadline passes while suspended: nothing closes, and it is not closed as unverified either.
        var runner = Scheduler();
        _time.Advance(TimeSpan.FromMinutes(2));
        (await runner.RunBatchAsync(CancellationToken.None)).Should().Be(0);
        (await EpisodeAsync(opened.EpisodeId)).HandlingState.Should().Be(HandlingState.New);

        // Coverage episodes never auto-resolve: no lifecycle timer was staged for it.
        (await JobsAsync(coverageEpisode.EpisodeId)).Should().NotContain(j => LifecycleScheduler.TimerKinds.Contains(j.Kind));

        // The canary resumes: one and two successes are not enough (flap protection), the third restores.
        for (var i = 1; i <= 3; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            await ProcessAsync(_covered, Heartbeat());
            var state = (await CoverageAsync(_covered.Integration.IntegrationId))!;
            state.State.Should().Be(i < 3 ? CoverageStates.Degraded : CoverageStates.Healthy, $"after success {i}");
        }
        (await CoverageEpisodesAsync()).Should().ContainSingle("one episode per outage");
        var resolvedCoverage = await EpisodeAsync(coverageEpisode.EpisodeId);
        resolvedCoverage.HandlingState.Should().Be(HandlingState.Closed);
        resolvedCoverage.ClosureReason.Should().Be(ClosureReason.SourceResolved);
        (await OutboxAsync(coverageEpisode.EpisodeId)).Should().Contain(o => o.Type == NotificationTypes.CoverageRestored);

        var resumed = await EpisodeAsync(opened.EpisodeId);
        resumed.ConditionState.Should().Be(ConditionState.Firing);
        (await JobsAsync(opened.EpisodeId, JobKinds.AutoResolve)).Should().ContainSingle().Which.Status.Should().Be(JobStatus.Pending);

        // The resumed timer is overdue: it closes now, but the evidence says coverage was not healthy throughout.
        (await runner.RunBatchAsync(CancellationToken.None)).Should().Be(1);
        var closed = await EpisodeAsync(opened.EpisodeId);
        closed.HandlingState.Should().Be(HandlingState.Closed);
        closed.ResolutionEvidence.Should().Be(Evidence.InactivityUnverified);
    }

    [Fact]
    public async Task Scenario_CoverageAlertFlaps_stays_one_episode_until_the_required_run_of_successes()
    {
        await ProcessAsync(_covered, Heartbeat());
        _time.Advance(TimeSpan.FromMinutes(16));
        (await TickCoverageAsync()).Should().ContainSingle(c => c.Step.Lost);
        (await CoverageEpisodesAsync()).Should().ContainSingle();

        foreach (var success in new[] { true, true, false, true, false, true, true })
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            await ProcessAsync(_covered, Heartbeat(fail: !success));
            (await CoverageAsync(_covered.Integration.IntegrationId))!.State.Should().Be(CoverageStates.Degraded);
        }
        (await CoverageEpisodesAsync()).Should().ContainSingle("flapping never opens a second coverage episode");

        _time.Advance(TimeSpan.FromMinutes(1));
        await ProcessAsync(_covered, Heartbeat());
        (await CoverageAsync(_covered.Integration.IntegrationId))!.State.Should().Be(CoverageStates.Healthy, "third consecutive success");
        (await CoverageEpisodesAsync()).Should().ContainSingle().Which.HandlingState.Should().Be(HandlingState.Closed);
    }

    [Fact]
    public async Task Scenario_IngestionOrMappingUnhealthy_postpones_inference()
    {
        var opened = await OpenAsync(_uncovered, "q7", "repeat");
        _time.Advance(TimeSpan.FromMinutes(10));
        for (var i = 0; i < 5; i++)
        {
            var result = await ProcessAsync(_uncovered, """{"nothing":"useful"}""");
            result.Outcome.Should().Be(ProcessingOutcome.MappingFailed);
        }
        _time.Advance(TimeSpan.FromMinutes(6));
        (await Scheduler().RunBatchAsync(CancellationToken.None)).Should().Be(1);

        var postponed = await EpisodeAsync(opened.EpisodeId);
        postponed.HandlingState.Should().Be(HandlingState.New, "closure is postponed while the source's mapping is unhealthy");
        postponed.AutoResolveAt.Should().Be(_time.GetUtcNow().AddMinutes(5));
        var note = (await TimelineAsync(opened.EpisodeId)).Should().Contain(e => e.Kind == EpisodeEventKind.Postpone).Which;
        note.Detail.Should().Contain("integration_unhealthy").And.Contain("mapping failures");
        (await JobsAsync(opened.EpisodeId, JobKinds.AutoResolve)).Should().Contain(j => j.Status == JobStatus.Pending && j.NotBefore == postponed.AutoResolveAt);
    }

    [Fact]
    public async Task Scenario_OldAlertReachesAdministrativeExpiry_reviewed_then_expired_with_condition_preserved()
    {
        var opened = await OpenAsync(_uncovered, "q8", "legacy");
        opened.LifecycleProfile.Should().Be(LifecycleProfiles.Unknown);
        opened.AutoResolveAt.Should().BeNull("this policy does not infer recovery");
        (await JobsAsync(opened.EpisodeId)).Select(j => j.Kind).Should().Contain(JobKinds.StaleReview).And.Contain(JobKinds.AdminExpiry).And.NotContain(JobKinds.AutoResolve);

        var runner = Scheduler();
        _time.Advance(TimeSpan.FromHours(24).Add(TimeSpan.FromMinutes(1)));
        (await runner.RunBatchAsync(CancellationToken.None)).Should().Be(1);
        var reviewed = await EpisodeAsync(opened.EpisodeId);
        reviewed.HandlingState.Should().Be(HandlingState.New);
        reviewed.StaleSince.Should().Be(_time.GetUtcNow(), "listed under Stale / unverified from now on");

        _time.Advance(TimeSpan.FromDays(6));
        (await runner.RunBatchAsync(CancellationToken.None)).Should().Be(1);
        var expired = await EpisodeAsync(opened.EpisodeId);
        expired.HandlingState.Should().Be(HandlingState.Closed);
        expired.ClosureReason.Should().Be(ClosureReason.ExpiredUnverified);
        expired.ResolutionEvidence.Should().Be(Evidence.None);
        expired.ConditionState.Should().Be(ConditionState.Firing, "expiry never rewrites the last known condition");
        (await TimelineAsync(opened.EpisodeId)).Should().Contain(e => e.Kind == EpisodeEventKind.Expire);
    }

    [Fact]
    public async Task Scenario_NewSignalRacesWithExpiry_a_stale_timer_cannot_close_the_updated_episode()
    {
        var opened = await OpenAsync(_uncovered, "q9", "race");
        var originalExpiry = (await JobsAsync(opened.EpisodeId, JobKinds.AdminExpiry)).Single();
        originalExpiry.NotBefore.Should().Be(opened.LastSeen.AddHours(1));

        // 59 minutes later the source fires again: last_seen moves, timers are re-staged from it.
        _time.Advance(TimeSpan.FromMinutes(59));
        (await ProcessAsync(_uncovered, Firing("q9", "race", at: _time.GetUtcNow()))).Outcome.Should().Be(ProcessingOutcome.Updated);
        var updated = await EpisodeAsync(opened.EpisodeId);
        updated.LastSeen.Should().Be(_time.GetUtcNow());
        var jobs = await JobsAsync(opened.EpisodeId, JobKinds.AdminExpiry);
        jobs.Single(j => j.JobId == originalExpiry.JobId).Status.Should().Be(JobStatus.Cancelled);
        jobs.Should().ContainSingle(j => j.Status == JobStatus.Pending && j.NotBefore == updated.LastSeen.AddHours(1));

        // A timer that still carries the pre-update guards fires now (the race: claimed just before the update): guards 1/2 refuse.
        await TestServices.InDbAsync(_services, db => db.Jobs.Where(j => j.EpisodeId == opened.EpisodeId && j.Kind == JobKinds.AdminExpiry && j.Status == JobStatus.Pending)
            .ExecuteUpdateAsync(u => u.SetProperty(j => j.NotBefore, _time.GetUtcNow()).SetProperty(j => j.ExpectedLastSeen, opened.LastSeen).SetProperty(j => j.ExpectedVersion, opened.Version)));
        _time.Advance(TimeSpan.FromMinutes(2));
        (await Scheduler().RunBatchAsync(CancellationToken.None)).Should().Be(1);
        var stillOpen = await EpisodeAsync(opened.EpisodeId);
        stillOpen.HandlingState.Should().Be(HandlingState.New);
        (await TimelineAsync(opened.EpisodeId)).Should().Contain(e => e.Kind == EpisodeEventKind.Postpone && e.Detail!.Contains("stale_timer"));
    }

    [Fact]
    public async Task Scenario_SourceResumesAfterExpiry_opens_a_new_episode_linked_to_the_expired_one()
    {
        var opened = await OpenAsync(_uncovered, "q10", "race");
        _time.Advance(TimeSpan.FromHours(1).Add(TimeSpan.FromMinutes(1)));
        (await Scheduler().RunBatchAsync(CancellationToken.None)).Should().Be(1);
        (await EpisodeAsync(opened.EpisodeId)).ClosureReason.Should().Be(ClosureReason.ExpiredUnverified);

        _time.Advance(TimeSpan.FromMinutes(5));
        var result = await ProcessAsync(_uncovered, Firing("q10", "race", at: _time.GetUtcNow()));
        result.Outcome.Should().Be(ProcessingOutcome.Opened);
        result.EpisodeId.Should().NotBe(opened.EpisodeId);
        var fresh = await EpisodeAsync(result.EpisodeId!.Value);
        fresh.PreviousEpisodeId.Should().Be(opened.EpisodeId);
        (await EpisodeAsync(opened.EpisodeId)).HandlingState.Should().Be(HandlingState.Closed, "the expired episode is untouched");
    }
}
