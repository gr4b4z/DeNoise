using System.Text;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Ingest;
using DeNoise.Application.Integrations;
using DeNoise.Application.Notifications;
using DeNoise.Application.Ops;
using DeNoise.Application.Processing;
using DeNoise.Application.Teams;
using DeNoise.Domain.Episodes;
using DeNoise.Domain.Notifications;
using DeNoise.Domain.Ops;
using DeNoise.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DeNoise.Integration.Tests.Milestone13;

/// <summary>
/// Spec §22 flap rows (§11.4 reopen window, §12.6 closure hold): <i>Condition flaps within the reopen window</i>, <i>Flapping condition is
/// acknowledged, then re-fires</i>, <i>Flapping stops</i>, <i>Human closed the episode, source re-fires</i>, <i>Reopen window expires before
/// the next firing</i>. Real processing and timers over a real database; the clock is a <see cref="FakeTimeProvider"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class FlapScenarios(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = PostgresFixture.TestEpoch;
    private string _cs = string.Empty;
    private FakeTimeProvider _time = null!;
    private ServiceProvider _services = null!;
    private IntegrationCredentials _flap = null!;   // reopen_window 10m, threshold 2, close_hold 5m, notify_on_reopen first (default)
    private IntegrationCredentials _quiet = null!;  // reopen_window 10m, notify_on_reopen never

    private const string FlapDefaults = """{"lifecycle":{"profile":"explicit_recovery","flap":{"reopen_window":"10m","threshold":2,"close_hold":"5m","hold_max":"30m"}}}""";
    private const string QuietDefaults = """{"lifecycle":{"profile":"explicit_recovery","flap":{"reopen_window":"10m","notify_on_reopen":"never"}}}""";

    public async Task InitializeAsync()
    {
        _cs = await postgres.CreateDatabaseAsync("flap");
        _time = new FakeTimeProvider(T0);
        _services = TestServices.Build(_cs, _time, settings: new Dictionary<string, string?> { ["Notifications:AllowInsecureDestinations"] = "true" });
        var actor = Actor.System("setup");
        await TestServices.InScopeAsync(_services, async sp =>
        {
            var team = await sp.GetRequiredService<TeamService>().CreateAsync(new CreateTeam("mpt", ["scope-a"], IsTriage: true), actor);
            await sp.GetRequiredService<DestinationService>().CreatePairAsync(
                new CreateDestination("hook", ChannelTypes.Webhook, team.TeamId, null, Url: "http://receiver.test/a"),
                new CreateDestination("hook-fallback", ChannelTypes.Webhook, team.TeamId, null, Url: "http://receiver.test/b"), actor);
            var integrations = sp.GetRequiredService<IntegrationService>();
            _flap = await integrations.CreateAsync(new CreateIntegration("flappy", "generic_webhook", "scope-a", team.TeamId, ProfileDefaults: FlapDefaults), actor);
            _quiet = await integrations.CreateAsync(new CreateIntegration("quiet", "generic_webhook", "scope-a", team.TeamId, ProfileDefaults: QuietDefaults), actor);
        });
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static string Body(string eventType, string alertId, DateTimeOffset at, string severity = "high")
        => $$"""{"eventType":"{{eventType}}","alertId":"{{alertId}}","eventId":"{{Guid.NewGuid()}}","occurredAt":"{{at:O}}","severity":"{{severity}}","environment":"production","service":"orders","resource":{"id":"res-{{alertId}}","name":"Orders"},"rule":{"id":"r-{{alertId}}","name":"flappy rule"},"summary":"{{alertId}} {{eventType}}"}""";

    private async Task<ProcessingResult> SendAsync(IntegrationCredentials integration, string eventType, string alertId, string severity = "high")
    {
        var body = Body(eventType, alertId, _time.GetUtcNow(), severity);
        var accepted = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<IngestService>()
            .AcceptAsync(integration.Integration, new IngestRequest(Encoding.UTF8.GetBytes(body), "application/json", new Dictionary<string, string>(), null)));
        var result = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<EventProcessor>()
            .ProcessAsync(new NormaliseJobPayload(accepted.EventId, accepted.ReceivedAt, integration.Integration.IntegrationId)));
        await TestServices.InDbAsync(_services, db => db.Jobs.Where(j => j.Kind == JobKinds.Normalise && j.Status == JobStatus.Pending && j.IntegrationId == integration.Integration.IntegrationId)
            .ExecuteUpdateAsync(u => u.SetProperty(j => j.Status, JobStatus.Done)));
        return result;
    }

    private Task<ProcessingResult> FireAsync(IntegrationCredentials integration, string alertId, string severity = "high") => SendAsync(integration, "firing", alertId, severity);
    private Task<ProcessingResult> ResolveAsync(IntegrationCredentials integration, string alertId) => SendAsync(integration, "resolved", alertId);

    private Task<Episode> EpisodeAsync(Guid id) => TestServices.InDbAsync(_services, db => db.Episodes.AsNoTracking().SingleAsync(e => e.EpisodeId == id));

    private Task<List<Episode>> EpisodesOfAsync(string alertId) => TestServices.InDbAsync(_services,
        db => db.Episodes.AsNoTracking().Where(e => e.SourceAlertId == alertId).OrderBy(e => e.CreatedAt).ToListAsync());

    private Task<List<OutboxMessage>> OutboxAsync(Guid episodeId, string type) => TestServices.InDbAsync(_services,
        db => db.Outbox.AsNoTracking().Where(o => o.EpisodeId == episodeId && o.Type == type).OrderBy(o => o.CreatedAt).ToListAsync());

    private Task<List<EpisodeEvent>> TimelineAsync(Guid episodeId, string kind) => TestServices.InDbAsync(_services,
        db => db.EpisodeEvents.AsNoTracking().Where(e => e.EpisodeId == episodeId && e.Kind == kind).OrderBy(e => e.At).ToListAsync());

    private Task<List<Job>> JobsAsync(Guid episodeId, string kind) => TestServices.InDbAsync(_services,
        db => db.Jobs.AsNoTracking().Where(j => j.EpisodeId == episodeId && j.Kind == kind).OrderBy(j => j.CreatedAt).ToListAsync());

    private Task MutateAsync(Guid episodeId, Action<Episode, DateTimeOffset> mutate)
        => TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<IProcessingUnitOfWork>().RunAsync<object?>(async session =>
        {
            var episode = await session.FindEpisodeAsync(episodeId) ?? throw new InvalidOperationException("episode vanished");
            mutate(episode, _time.GetUtcNow());
            return null;
        }));

    private async Task RunFlapHoldJobsAsync()
    {
        var claimed = await TestServices.InQueueAsync(_services, q => q.ClaimAsync([JobKinds.FlapHold], "sched", TimeSpan.FromMinutes(1), 10));
        foreach (var job in claimed)
        {
            await TestServices.InScopeAsync(_services, async sp =>
            {
                var handler = sp.GetServices<IJobHandler>().Single(h => h.Kind == JobKinds.FlapHold);
                await handler.HandleAsync(job, new JobContext("sched", (_, _) => Task.FromResult(true)), CancellationToken.None);
                await sp.GetRequiredService<IJobQueue>().CompleteAsync(job.JobId, "sched");
            });
        }
    }

    // ---- scenarios -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Scenario_ConditionFlapsWithinTheReopenWindow_one_episode_with_a_reopen_count_and_one_opening_notification()
    {
        var opened = await FireAsync(_flap, "flap-1");
        opened.Outcome.Should().Be(ProcessingOutcome.Opened);
        var id = opened.EpisodeId!.Value;
        (await OutboxAsync(id, NotificationTypes.EpisodeOpened)).Should().HaveCount(2, "the team's two destinations are told once about the opening");

        _time.Advance(TimeSpan.FromMinutes(1));
        (await ResolveAsync(_flap, "flap-1")).Outcome.Should().Be(ProcessingOutcome.Resolved);
        _time.Advance(TimeSpan.FromMinutes(1));
        var first = await FireAsync(_flap, "flap-1");
        first.Outcome.Should().Be(ProcessingOutcome.Reopened);
        first.EpisodeId.Should().Be(id, "the firing inside the reopen window rejoins the closed episode (spec §11.4)");

        _time.Advance(TimeSpan.FromMinutes(1));
        (await ResolveAsync(_flap, "flap-1")).Outcome.Should().Be(ProcessingOutcome.Resolved);
        _time.Advance(TimeSpan.FromMinutes(1));
        var second = await FireAsync(_flap, "flap-1");
        second.Outcome.Should().Be(ProcessingOutcome.Reopened);
        second.EpisodeId.Should().Be(id);

        (await EpisodesOfAsync("flap-1")).Should().ContainSingle("the whole flap chain is one episode");
        var episode = await EpisodeAsync(id);
        episode.IsOpen.Should().BeTrue();
        episode.ConditionState.Should().Be(ConditionState.Firing);
        episode.ReopenCount.Should().Be(2);
        episode.IsFlapping.Should().BeTrue("threshold is 2");
        episode.OccurrenceCount.Should().Be(3, "no signal is lost to the reopen");
        episode.ClosureReason.Should().BeNull();
        episode.FlapWindowStartedAt.Should().Be(T0 + TimeSpan.FromMinutes(2));

        (await OutboxAsync(id, NotificationTypes.EpisodeOpened)).Should().HaveCount(4, "open + the first reopen only (notify_on_reopen: first), not one per cycle");
        var reopens = await TimelineAsync(id, EpisodeEventKind.Reopen);
        reopens.Should().HaveCount(2);
        reopens[0].Detail.Should().Contain("\"reopenCount\": 1").And.Contain("\"closureReason\": \"source_resolved\"").And.Contain("\"reopenWindow\": \"00:10:00\"");
        reopens[1].Detail.Should().Contain("\"flapping\": true");
        var audits = await TestServices.InDbAsync(_services, db => db.AuditEntries.AsNoTracking().Where(a => a.TargetId == id.ToString() && a.Action == "episode.reopen").CountAsync());
        audits.Should().Be(2, "every reopen is in the audit trail");
        var pendingTimers = await TestServices.InDbAsync(_services, db => db.Jobs.AsNoTracking().CountAsync(j => j.EpisodeId == id && j.Kind == JobKinds.AutoResolve && j.Status == JobStatus.Pending));
        pendingTimers.Should().Be(1, "lifecycle timers restart from the new signal");
    }

    [Fact]
    public async Task Scenario_FlappingConditionIsAcknowledgedThenRefires_acknowledgement_and_assignee_survive_without_a_new_notification()
    {
        var opened = await FireAsync(_quiet, "ack-1");
        var id = opened.EpisodeId!.Value;
        var operatorId = Guid.NewGuid();
        var ackAt = _time.GetUtcNow();
        await MutateAsync(id, (e, now) => e.Acknowledge(operatorId, now));
        var before = await EpisodeAsync(id);
        before.HandlingState.Should().Be(HandlingState.Acknowledged);

        _time.Advance(TimeSpan.FromMinutes(1));
        (await ResolveAsync(_quiet, "ack-1")).Outcome.Should().Be(ProcessingOutcome.Resolved);
        _time.Advance(TimeSpan.FromMinutes(2));
        (await FireAsync(_quiet, "ack-1")).Outcome.Should().Be(ProcessingOutcome.Reopened);

        var episode = await EpisodeAsync(id);
        episode.HandlingState.Should().Be(HandlingState.Acknowledged, "acknowledgement survives the reopen (spec §11.4)");
        episode.AssigneeId.Should().Be(operatorId);
        episode.AcknowledgedBy.Should().Be(operatorId);
        episode.AcknowledgedAt.Should().Be(ackAt);
        episode.AckDeadlineAt.Should().BeNull("an acknowledged episode has no deadline; nothing is restarted");
        (await JobsAsync(id, JobKinds.AckDeadline)).Should().NotContain(j => j.Status == JobStatus.Pending, "no fresh acknowledgement is demanded");
        (await OutboxAsync(id, NotificationTypes.EpisodeOpened)).Should().HaveCount(2, "notify_on_reopen: never — the opening notification is not repeated");
        (await TimelineAsync(id, EpisodeEventKind.Notify)).Should().ContainSingle(e => e.Detail!.Contains("notify_on_reopen is 'never'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Scenario_FlappingStops_closure_notification_goes_out_after_the_hold_and_a_refire_inside_the_hold_cancels_it()
    {
        var id = (await FireAsync(_flap, "stop-1")).EpisodeId!.Value;
        // Two cycles make the episode flapping (threshold 2); their closures notify normally because the label was not set yet.
        for (var i = 0; i < 2; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            (await ResolveAsync(_flap, "stop-1")).Outcome.Should().Be(ProcessingOutcome.Resolved);
            _time.Advance(TimeSpan.FromMinutes(1));
            (await FireAsync(_flap, "stop-1")).Outcome.Should().Be(ProcessingOutcome.Reopened);
        }
        (await EpisodeAsync(id)).IsFlapping.Should().BeTrue();
        var closedBefore = (await OutboxAsync(id, NotificationTypes.EpisodeClosed)).Count;
        closedBefore.Should().Be(4, "the two closures before the label notified the two destinations each");

        // Closure of a flapping episode: recorded immediately, notification held.
        _time.Advance(TimeSpan.FromMinutes(1));
        (await ResolveAsync(_flap, "stop-1")).Outcome.Should().Be(ProcessingOutcome.Resolved);
        var heldAt = _time.GetUtcNow();
        var held = await EpisodeAsync(id);
        held.IsOpen.Should().BeFalse("the closure itself is never delayed (spec §12.6)");
        held.ClosureReason.Should().Be(ClosureReason.SourceResolved);
        held.ResolutionEvidence.Should().Be(Evidence.Source);
        held.ClosureHoldUntil.Should().Be(heldAt + TimeSpan.FromMinutes(5));
        (await OutboxAsync(id, NotificationTypes.EpisodeClosed)).Should().HaveCount(closedBefore, "the closure notification is held, not staged");
        (await JobsAsync(id, JobKinds.FlapHold)).Should().ContainSingle(j => j.Status == JobStatus.Pending && j.NotBefore == heldAt + TimeSpan.FromMinutes(5));

        // A re-fire inside the hold cancels the held notification and reopens.
        _time.Advance(TimeSpan.FromMinutes(2));
        (await FireAsync(_flap, "stop-1")).Outcome.Should().Be(ProcessingOutcome.Reopened);
        (await JobsAsync(id, JobKinds.FlapHold)).Should().OnlyContain(j => j.Status == JobStatus.Cancelled);
        (await EpisodeAsync(id)).ClosureHoldUntil.Should().BeNull();
        (await TimelineAsync(id, EpisodeEventKind.FlapHold)).Select(e => e.Detail).Should().Contain(d => d!.Contains("\"action\": \"cancelled\"", StringComparison.Ordinal));
        await RunFlapHoldJobsAsync();
        (await OutboxAsync(id, NotificationTypes.EpisodeClosed)).Should().HaveCount(closedBefore, "a cancelled hold sends nothing");

        // Flapping stops: the hold elapses with the condition still resolved and the closure is emitted with its normal evidence.
        _time.Advance(TimeSpan.FromMinutes(1));
        (await ResolveAsync(_flap, "stop-1")).Outcome.Should().Be(ProcessingOutcome.Resolved);
        var secondHold = (await EpisodeAsync(id)).ClosureHoldUntil;
        secondHold.Should().Be(_time.GetUtcNow() + TimeSpan.FromMinutes(5));
        _time.Advance(TimeSpan.FromMinutes(6));
        await RunFlapHoldJobsAsync();

        var after = await EpisodeAsync(id);
        after.IsOpen.Should().BeFalse();
        after.ClosureHoldUntil.Should().BeNull();
        after.ClosureReason.Should().Be(ClosureReason.SourceResolved);
        after.ResolutionEvidence.Should().Be(Evidence.Source);
        var closed = await OutboxAsync(id, NotificationTypes.EpisodeClosed);
        closed.Should().HaveCount(closedBefore + 2, "the held closure reaches the prior recipients once the hold elapsed");
        closed[^1].Payload.Should().Contain("\"flapping\": true").And.Contain("\"reason\": \"source_resolved\"");
        (await TimelineAsync(id, EpisodeEventKind.FlapHold)).Select(e => e.Detail).Should().Contain(d => d!.Contains("\"action\": \"released\"", StringComparison.Ordinal));
        (await JobsAsync(id, JobKinds.FlapHold)).Should().OnlyContain(j => j.Status != JobStatus.Pending);
    }

    [Fact]
    public async Task Scenario_HumanClosedTheEpisodeSourceRefires_a_new_episode_is_opened_never_a_reopen()
    {
        var first = (await FireAsync(_flap, "human-1")).EpisodeId!.Value;
        _time.Advance(TimeSpan.FromMinutes(1));
        await MutateAsync(first, (e, now) => e.CloseManually("known issue, ticket filed", now));
        (await EpisodeAsync(first)).ClosureReason.Should().Be(ClosureReason.ManualClose);

        _time.Advance(TimeSpan.FromMinutes(1));
        var refire = await FireAsync(_flap, "human-1");
        refire.Outcome.Should().Be(ProcessingOutcome.Opened, "a human decision stays decided (spec §11.4)");
        refire.EpisodeId.Should().NotBe(first);
        var episodes = await EpisodesOfAsync("human-1");
        episodes.Should().HaveCount(2);
        episodes[1].PreviousEpisodeId.Should().Be(first, "recurrence under §11.3 links to the same identity");
        episodes[1].ReopenCount.Should().Be(0);
        episodes[0].ClosureReason.Should().Be(ClosureReason.ManualClose, "the closed episode is untouched");
    }

    [Fact]
    public async Task Scenario_ReopenWindowExpiresBeforeTheNextFiring_a_new_episode_linked_to_the_previous_one()
    {
        var first = (await FireAsync(_flap, "late-1")).EpisodeId!.Value;
        _time.Advance(TimeSpan.FromMinutes(1));
        (await ResolveAsync(_flap, "late-1")).Outcome.Should().Be(ProcessingOutcome.Resolved);

        _time.Advance(TimeSpan.FromMinutes(11));
        var refire = await FireAsync(_flap, "late-1");
        refire.Outcome.Should().Be(ProcessingOutcome.Opened, "11 minutes after closure is outside the 10-minute window: recurrence, as §11.3 requires");
        refire.EpisodeId.Should().NotBe(first);
        var episodes = await EpisodesOfAsync("late-1");
        episodes.Should().HaveCount(2);
        episodes[1].PreviousEpisodeId.Should().Be(first);
        episodes[1].HandlingState.Should().Be(HandlingState.New, "a new episode comes with a new acknowledgement requirement");
        (await OutboxAsync(refire.EpisodeId!.Value, NotificationTypes.EpisodeOpened)).Should().HaveCount(2);
    }
}
