using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Ingest;
using AlertHub.Application.Integrations;
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
using AlertHub.Infrastructure.Persistence;
using AlertHub.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AlertHub.Integration.Tests.Milestone3;

/// <summary>
/// Spec §22: <i>No ownership rule matches</i>, <i>Worker fails after state commit</i> (outbox part),
/// <i>Delivery succeeds but response is lost</i>; plus routing, ack-deadline escalation, closure to prior recipients,
/// coalescing and fallback. The webhook channel is replaced by a scripted fake; the outbox, dispatcher and
/// database are real.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RoutingAndOutboxScenarios(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    private string _cs = string.Empty;
    private FakeTimeProvider _time = null!;
    private ScriptedChannel _channel = null!;
    private ServiceProvider _services = null!;
    private IntegrationCredentials _integration = null!;
    private Guid _teamA;
    private Guid _triage;
    private Guid _destA;
    private Guid _destAFallback;
    private Guid _destTriage;
    private Guid _escalation;

    public async Task InitializeAsync()
    {
        _cs = await postgres.CreateDatabaseAsync("routing");
        _time = new FakeTimeProvider(T0);
        _channel = new ScriptedChannel();
        _services = TestServices.Build(_cs, _time, s =>
        {
            s.RemoveAll<INotificationChannel>();
            s.AddSingleton<INotificationChannel>(_channel);
        }, new Dictionary<string, string?> { ["Notifications:AllowInsecureDestinations"] = "true", ["Notifications:PublicBaseUrl"] = "https://hub.test" });
        _integration = await TestServices.CreateIntegrationAsync(_services, "generic", "generic_webhook", "scope-a");

        var actor = Actor.System("setup");
        await TestServices.InScopeAsync(_services, async sp =>
        {
            var teams = sp.GetRequiredService<TeamService>();
            var teamA = await teams.CreateAsync(new CreateTeam("mpt-devops", ["scope-a"]), actor);
            var triage = await teams.CreateAsync(new CreateTeam("triage", ["scope-a"], IsTriage: true), actor);
            _teamA = teamA.TeamId;
            _triage = triage.TeamId;

            var destinations = sp.GetRequiredService<DestinationService>();
            var (primary, fallback) = await destinations.CreatePairAsync(
                new CreateDestination("mpt-teams", ChannelTypes.Webhook, _teamA, null, Url: "http://receiver.test/mpt"),
                new CreateDestination("mpt-fallback", ChannelTypes.Webhook, _teamA, null, Url: "http://receiver.test/mpt-fallback"), actor);
            _destA = primary.Destination.DestinationId;
            _destAFallback = fallback.Destination.DestinationId;
            var (triageDest, _) = await destinations.CreatePairAsync(
                new CreateDestination("triage-hook", ChannelTypes.Webhook, _triage, null, Url: "http://receiver.test/triage"),
                new CreateDestination("triage-fallback", ChannelTypes.Webhook, _triage, null, Url: "http://receiver.test/triage-fallback"), actor);
            _destTriage = triageDest.Destination.DestinationId;

            var policies = sp.GetRequiredService<PolicyService>();
            var escalation = await policies.CreateVersionAsync(new CreatePolicyVersion(PolicyKinds.Escalation, """
                escalation:
                  ack_deadline: 15m
                  steps:
                    - { after: 0m, targets: [team_destinations] }
                    - { after: 15m, targets: [team_destinations] }
                  repeat_last_step_every: 30m
                  max_repeats: 1
                """, Name: "default"), actor);
            await policies.ActivateAsync(PolicyKinds.Escalation, escalation.PolicyId, 1, actor);
            _escalation = escalation.PolicyId;

            var routing = await policies.CreateVersionAsync(new CreatePolicyVersion(PolicyKinds.Routing, $$"""
                routing:
                  rules:
                    - name: mpt-prod
                      priority: 10
                      match: { all: [ { eq: [environment, production] }, { eq: [service, orders] } ] }
                      team: {{_teamA}}
                      escalation_policy: {{_escalation}}
                """), actor);
            await policies.ActivateAsync(PolicyKinds.Routing, routing.PolicyId, 1, actor);
        });
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    private static string Firing(string alertId, string eventId, DateTimeOffset at, string service = "orders", string severity = "high")
        => $$"""{"eventType":"firing","alertId":"{{alertId}}","eventId":"{{eventId}}","occurredAt":"{{at:O}}","severity":"{{severity}}","environment":"production","service":"{{service}}","resource":{"id":"res-{{alertId}}","name":"Orders"},"rule":{"id":"5xx","name":"5xx rate"},"summary":"5xx high" }""";

    private static string Resolved(string alertId, string eventId, DateTimeOffset at, string service = "orders")
        => $$"""{"eventType":"resolved","alertId":"{{alertId}}","eventId":"{{eventId}}","occurredAt":"{{at:O}}","severity":"high","environment":"production","service":"{{service}}","resource":{"id":"res-{{alertId}}"},"rule":{"id":"5xx"} }""";

    private async Task<ProcessingResult> IngestAndProcessAsync(string body)
    {
        var accepted = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<IngestService>()
            .AcceptAsync(_integration.Integration, new IngestRequest(Encoding.UTF8.GetBytes(body), "application/json", new Dictionary<string, string>(), null)));
        return await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<EventProcessor>()
            .ProcessAsync(new NormaliseJobPayload(accepted.EventId, accepted.ReceivedAt, _integration.Integration.IntegrationId)));
    }

    private Task<IReadOnlyList<(OutboxMessage Message, DispatchOutcome Outcome)>> DispatchAsync(string worker = "dispatcher-1")
        => TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(worker, 50, CancellationToken.None));

    private Task<List<OutboxMessage>> OutboxAsync(Guid? episodeId = null) => TestServices.InDbAsync(_services,
        db => db.Outbox.AsNoTracking().Where(o => episodeId == null || o.EpisodeId == episodeId).OrderBy(o => o.CreatedAt).ThenBy(o => o.Type).ToListAsync());

    private Task<Episode> EpisodeAsync(Guid id) => TestServices.InDbAsync(_services, db => db.Episodes.AsNoTracking().SingleAsync(e => e.EpisodeId == id));

    [Fact]
    public async Task Matching_rule_assigns_the_team_stages_opened_notifications_and_schedules_the_ack_deadline()
    {
        var result = await IngestAndProcessAsync(Firing("a1", "e1", T0));

        result.Outcome.Should().Be(ProcessingOutcome.Opened);
        var episode = await EpisodeAsync(result.EpisodeId!.Value);
        episode.OwningTeamId.Should().Be(_teamA);
        episode.RoutingRuleId.Should().NotBeNull();
        episode.RoutingCorrectionRequired.Should().BeFalse();
        episode.AckDeadlineAt.Should().Be(T0.AddMinutes(15));

        var outbox = await OutboxAsync(episode.EpisodeId);
        outbox.Should().HaveCount(2, "one row per subscribed team destination (primary + fallback are both team destinations)");
        outbox.Should().OnlyContain(o => o.Type == NotificationTypes.EpisodeOpened && o.Status == OutboxStatus.Pending);
        outbox.Select(o => o.DestinationId).Should().BeEquivalentTo([_destA, _destAFallback]);
        var payload = JsonNode.Parse(outbox[0].Payload)!;
        payload["episode"]!["url"]!.ToString().Should().Be($"https://hub.test/episodes/{episode.EpisodeId}");
        payload["episode"]!["owningTeam"]!["name"]!.ToString().Should().Be("mpt-devops");

        await using var db = PostgresFixture.CreateContext(_cs);
        var timer = await db.Jobs.SingleAsync(j => j.EpisodeId == episode.EpisodeId && j.Kind == JobKinds.AckDeadline);
        timer.NotBefore.Should().Be(T0.AddMinutes(15));
        timer.Status.Should().Be(JobStatus.Pending);
        (await db.EpisodeEvents.CountAsync(e => e.EpisodeId == episode.EpisodeId && e.Kind == EpisodeEventKind.Assign)).Should().Be(1);
    }

    [Fact]
    public async Task Scenario_NoOwnershipRuleMatches_goes_to_triage_flagged_with_a_routing_failure_notification()
    {
        var result = await IngestAndProcessAsync(Firing("a2", "e2", T0, service: "unknown-service"));

        var episode = await EpisodeAsync(result.EpisodeId!.Value);
        episode.OwningTeamId.Should().Be(_triage);
        episode.RoutingCorrectionRequired.Should().BeTrue();
        episode.RoutingRuleId.Should().BeNull();
        episode.AssigneeId.Should().BeNull("visibly unassigned");

        var outbox = await OutboxAsync(episode.EpisodeId);
        outbox.Select(o => o.Type).Should().Contain(NotificationTypes.EpisodeRoutingFailure).And.Contain(NotificationTypes.EpisodeOpened);
        outbox.Where(o => o.Type == NotificationTypes.EpisodeRoutingFailure).Select(o => o.DestinationId).Should().Contain(_destTriage);

        await using var db = PostgresFixture.CreateContext(_cs);
        (await db.AuditEntries.CountAsync(a => a.Action == "episode.routing_failure" && a.TargetId == episode.EpisodeId.ToString())).Should().Be(1);
    }

    [Fact]
    public async Task Scenario_WorkerFailsAfterStateCommit_outbox_rows_survive_and_the_dispatcher_delivers_them()
    {
        var result = await IngestAndProcessAsync(Firing("a3", "e3", T0));
        // The processing worker "dies" here: nothing else ran after the commit. The outbox rows must be waiting.
        (await OutboxAsync(result.EpisodeId)).Should().OnlyContain(o => o.Status == OutboxStatus.Pending);

        _channel.Script.Enqueue(_ => ChannelResult.Success(200, "ok"));
        _channel.Script.Enqueue(_ => ChannelResult.Success(200, "ok"));
        var dispatched = await DispatchAsync();

        dispatched.Should().HaveCount(2).And.OnlyContain(d => d.Outcome == DispatchOutcome.Sent);
        (await OutboxAsync(result.EpisodeId)).Should().OnlyContain(o => o.Status == OutboxStatus.Sent && o.SentAt != null);
        _channel.Sent.Should().HaveCount(2);
        var body = JsonNode.Parse(_channel.Sent[0].Body)!;
        body["deliveryId"]!.ToString().Should().Be(_channel.Sent[0].Message.OutboxId.ToString());
        body["event"]!.ToString().Should().Be(NotificationTypes.EpisodeOpened);
        body["sentAt"].Should().NotBeNull();

        await using var db = PostgresFixture.CreateContext(_cs);
        (await db.DeliveryAttempts.CountAsync(a => a.Outcome == DeliveryOutcomes.Success)).Should().Be(2);
        (await db.Destinations.SingleAsync(d => d.DestinationId == _destA)).LastSuccessAt.Should().Be(T0);
    }

    [Fact]
    public async Task Scenario_DeliverySucceedsButResponseIsLost_retry_is_controlled_and_keeps_the_same_delivery_id()
    {
        var result = await IngestAndProcessAsync(Firing("a4", "e4", T0));
        var rows = await OutboxAsync(result.EpisodeId);
        var target = rows.Single(o => o.DestinationId == _destA);
        // Make the other row irrelevant by giving it a success.
        _channel.Script.Enqueue(m => m.OutboxId == target.OutboxId ? ChannelResult.ResponseLost("timed out waiting for response") : ChannelResult.Success(200));
        _channel.Script.Enqueue(m => m.OutboxId == target.OutboxId ? ChannelResult.ResponseLost("timed out waiting for response") : ChannelResult.Success(200));

        var first = await DispatchAsync();
        first.Single(d => d.Message.OutboxId == target.OutboxId).Outcome.Should().Be(DispatchOutcome.Rescheduled);
        var afterFirst = (await OutboxAsync(result.EpisodeId)).Single(o => o.OutboxId == target.OutboxId);
        afterFirst.Status.Should().Be(OutboxStatus.Pending);
        afterFirst.Attempts.Should().Be(1);
        afterFirst.NotBefore.Should().Be(T0.AddSeconds(30), "first retry after 30 s (ADR-7)");

        (await DispatchAsync()).Should().BeEmpty("not due yet");
        _time.Advance(TimeSpan.FromSeconds(31));
        _channel.Script.Enqueue(_ => ChannelResult.Success(200));
        var second = await DispatchAsync();
        second.Single().Outcome.Should().Be(DispatchOutcome.Sent);

        var deliveries = _channel.Sent.Where(s => s.Message.OutboxId == target.OutboxId).ToList();
        deliveries.Should().HaveCount(2);
        deliveries.Select(s => JsonNode.Parse(s.Body)!["deliveryId"]!.ToString()).Distinct().Should().ContainSingle()
            .Which.Should().Be(target.OutboxId.ToString(), "receivers deduplicate on the stable delivery id");

        await using var db = PostgresFixture.CreateContext(_cs);
        var attempts = await db.DeliveryAttempts.Where(a => a.OutboxId == target.OutboxId).OrderBy(a => a.AttemptedAt).ToListAsync();
        attempts.Select(a => a.Outcome).Should().Equal(DeliveryOutcomes.ResponseLost, DeliveryOutcomes.Success);
        attempts[0].Error.Should().Contain("timed out");
    }

    [Fact]
    public async Task Permanent_failure_uses_the_fallback_destination_once_and_raises_hub_delivery_failure()
    {
        var result = await IngestAndProcessAsync(Firing("a5", "e5", T0));
        var rows = await OutboxAsync(result.EpisodeId);
        var toPrimary = rows.Single(o => o.DestinationId == _destA);
        _channel.Script.Enqueue(m => m.DestinationId == _destA ? ChannelResult.Permanent("HTTP 404", 404, "gone") : ChannelResult.Success(200));
        _channel.Script.Enqueue(m => m.DestinationId == _destA ? ChannelResult.Permanent("HTTP 404", 404, "gone") : ChannelResult.Success(200));

        var first = await DispatchAsync();
        first.Single(d => d.Message.OutboxId == toPrimary.OutboxId).Outcome.Should().Be(DispatchOutcome.FailedWithFallback);

        var afterFailure = await OutboxAsync(result.EpisodeId);
        afterFailure.Single(o => o.OutboxId == toPrimary.OutboxId).Status.Should().Be(OutboxStatus.Failed);
        var fallbackRows = afterFailure.Where(o => o.DestinationId == _destAFallback && o.Status == OutboxStatus.Pending).ToList();
        fallbackRows.Select(o => o.Type).Should().BeEquivalentTo([NotificationTypes.EpisodeOpened, NotificationTypes.HubDeliveryFailure]);

        _channel.Script.Enqueue(_ => ChannelResult.Success(200));
        _channel.Script.Enqueue(_ => ChannelResult.Success(200));
        var second = await DispatchAsync();
        second.Should().HaveCount(2).And.OnlyContain(d => d.Outcome == DispatchOutcome.Sent);

        await using var db = PostgresFixture.CreateContext(_cs);
        var fallbackAttempts = await db.DeliveryAttempts.Where(a => fallbackRows.Select(r => r.OutboxId).Contains(a.OutboxId)).ToListAsync();
        fallbackAttempts.Should().HaveCount(2).And.OnlyContain(a => a.UsedFallback);
        (await db.DeliveryAttempts.SingleAsync(a => a.OutboxId == toPrimary.OutboxId)).ResponseExcerpt.Should().Be("gone");
        (await db.Destinations.SingleAsync(d => d.DestinationId == _destA)).ConsecutiveFailures.Should().Be(1);
    }

    [Fact]
    public async Task Closure_notifies_prior_recipients_and_coalesces_stale_open_notifications()
    {
        var opened = await IngestAndProcessAsync(Firing("a6", "e6", T0));
        // Only deliver to the primary destination; leave the fallback's "opened" row pending.
        var rows = await OutboxAsync(opened.EpisodeId);
        var pendingFallbackRow = rows.Single(o => o.DestinationId == _destAFallback);
        await TestServices.InDbAsync(_services, db => db.Outbox.Where(o => o.OutboxId == pendingFallbackRow.OutboxId)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.NotBefore, T0.AddHours(1))));
        _channel.Script.Enqueue(_ => ChannelResult.Success(200));
        (await DispatchAsync()).Should().ContainSingle(d => d.Outcome == DispatchOutcome.Sent);

        var resolved = await IngestAndProcessAsync(Resolved("a6", "e7", T0.AddMinutes(5)));
        resolved.Outcome.Should().Be(ProcessingOutcome.Resolved);

        var afterClose = await OutboxAsync(opened.EpisodeId);
        afterClose.Where(o => o.Type == NotificationTypes.EpisodeClosed).Select(o => o.DestinationId).Should().BeEquivalentTo([_destA, _destAFallback], "both destinations had rows staged, so both are prior recipients");

        await using (var db = PostgresFixture.CreateContext(_cs))
        {
            (await db.Jobs.Where(j => j.EpisodeId == opened.EpisodeId && j.Kind == JobKinds.AckDeadline).Select(j => j.Status).SingleAsync()).Should().Be(JobStatus.Cancelled);
        }

        _time.Advance(TimeSpan.FromHours(2));
        _channel.Script.Enqueue(_ => ChannelResult.Success(200));
        _channel.Script.Enqueue(_ => ChannelResult.Success(200));
        var dispatched = await DispatchAsync();
        dispatched.Single(d => d.Message.OutboxId == pendingFallbackRow.OutboxId).Outcome.Should().Be(DispatchOutcome.Coalesced, "an old firing notification must never arrive after recovery as if current");
        dispatched.Where(d => d.Message.Type == NotificationTypes.EpisodeClosed).Should().HaveCount(2).And.OnlyContain(d => d.Outcome == DispatchOutcome.Sent);
    }

    [Fact]
    public async Task Ack_deadline_fires_ack_overdue_and_schedules_the_next_step_unless_acknowledged()
    {
        var overdue = await IngestAndProcessAsync(Firing("a7", "e8", T0));
        var acked = await IngestAndProcessAsync(Firing("a8", "e9", T0));
        await TestServices.InDbAsync(_services, async db =>
        {
            var e = await db.Episodes.SingleAsync(x => x.EpisodeId == acked.EpisodeId);
            e.Acknowledge(Guid.NewGuid(), T0.AddMinutes(1));
            return await db.SaveChangesAsync();
        });

        var runner = new JobRunner(_services.GetRequiredService<IServiceScopeFactory>(), _services.GetRequiredService<IOptions<JobQueueOptions>>(),
            _time, _services.GetRequiredService<AlertHubMetrics>(), _services.GetRequiredService<ILogger<JobRunner>>(), JobKinds.Scheduler, "scheduler");
        (await runner.RunBatchAsync(CancellationToken.None)).Should().Be(0, "deadlines are 15 minutes away");
        _time.Advance(TimeSpan.FromMinutes(16));
        (await runner.RunBatchAsync(CancellationToken.None)).Should().Be(2);

        var overdueRows = await OutboxAsync(overdue.EpisodeId);
        overdueRows.Where(o => o.Type == NotificationTypes.EpisodeAckOverdue).Should().HaveCount(2);
        JsonNode.Parse(overdueRows.First(o => o.Type == NotificationTypes.EpisodeAckOverdue).Payload)!["escalation"]!["step"]!.GetValue<int>().Should().Be(1);
        (await OutboxAsync(acked.EpisodeId)).Should().NotContain(o => o.Type == NotificationTypes.EpisodeAckOverdue, "acknowledgement stops ack escalation");

        await using var db = PostgresFixture.CreateContext(_cs);
        var next = await db.Jobs.SingleAsync(j => j.EpisodeId == overdue.EpisodeId && j.Kind == JobKinds.EscalationStep && j.Status == JobStatus.Pending);
        next.NotBefore.Should().Be(T0.AddMinutes(16).AddMinutes(15));
        (await db.EpisodeEvents.CountAsync(e => e.EpisodeId == overdue.EpisodeId && e.Kind == EpisodeEventKind.Escalate)).Should().Be(1);
    }

    [Fact]
    public async Task Shadow_integrations_are_routed_but_never_notify()
    {
        await TestServices.InDbAsync(_services, db => db.Integrations.Where(i => i.IntegrationId == _integration.Integration.IntegrationId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Shadow, true)));
        var result = await IngestAndProcessAsync(Firing("a9", "e10", T0));
        (await EpisodeAsync(result.EpisodeId!.Value)).OwningTeamId.Should().Be(_teamA);
        (await OutboxAsync(result.EpisodeId)).Should().BeEmpty();
    }

    private sealed class ScriptedChannel : INotificationChannel
    {
        public string ChannelType => ChannelTypes.Webhook;
        public ConcurrentQueue<Func<OutboxMessage, ChannelResult>> Script { get; } = new();
        public List<(OutboxMessage Message, string Body, ResolvedDestination Destination)> Sent { get; } = [];

        public Task<ChannelResult> SendAsync(ResolvedDestination destination, OutboxMessage message, string body, CancellationToken ct)
        {
            lock (Sent) Sent.Add((message, body, destination));
            return Task.FromResult(Script.TryDequeue(out var step) ? step(message) : ChannelResult.Success(200));
        }
    }
}
