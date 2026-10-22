using System.Text;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Ingest;
using DeNoise.Application.Integrations;
using DeNoise.Application.Notifications;
using DeNoise.Application.Policies;
using DeNoise.Application.Processing;
using DeNoise.Application.Teams;
using DeNoise.Domain.Common;
using DeNoise.Domain.Episodes;
using DeNoise.Domain.Notifications;
using DeNoise.Domain.Ops;
using DeNoise.Domain.Policies;
using DeNoise.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DeNoise.Integration.Tests.Milestone16;

/// <summary>
/// Spec §9 severity table: a <c>low</c> episode is queue only — owned and routed, but no opening notification and no acknowledgement
/// deadline; when its severity rises out of <c>low</c> it is announced as a new actionable episode and gets its deadline then; its
/// closure reaches the recipients it had.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class QueueOnlySeverityScenarios(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = PostgresFixture.TestEpoch;
    private string _cs = string.Empty;
    private FakeTimeProvider _time = null!;
    private ServiceProvider _services = null!;
    private Guid _teamId;
    private IntegrationCredentials _integration = null!;

    public async Task InitializeAsync()
    {
        _cs = await postgres.CreateDatabaseAsync("queueonly");
        _time = new FakeTimeProvider(T0);
        _services = TestServices.Build(_cs, _time, settings: new Dictionary<string, string?> { ["Notifications:AllowInsecureDestinations"] = "true" });
        var actor = Actor.System("setup");
        await TestServices.InScopeAsync(_services, async sp =>
        {
            var policies = sp.GetRequiredService<PolicyService>();
            var escalation = await policies.CreateVersionAsync(new CreatePolicyVersion(PolicyKinds.Escalation, """
                escalation:
                  ack_deadline: 15m
                  steps:
                    - { after: 0m, targets: [team_destinations] }
                """, Name: "default"), actor);
            await policies.ActivateAsync(PolicyKinds.Escalation, escalation.PolicyId, 1, actor);
            var team = await sp.GetRequiredService<TeamService>().CreateAsync(new CreateTeam("hygiene", ["scope-a"], IsTriage: true, DefaultEscalationPolicyId: escalation.PolicyId), actor);
            _teamId = team.TeamId;
            await sp.GetRequiredService<DestinationService>().CreatePairAsync(
                new CreateDestination("hook", ChannelTypes.Webhook, team.TeamId, null, Url: "http://receiver.test/a"),
                new CreateDestination("hook-fallback", ChannelTypes.Webhook, team.TeamId, null, Url: "http://receiver.test/b"), actor);
            var routing = await policies.CreateVersionAsync(new CreatePolicyVersion(PolicyKinds.Routing, $$"""
                routing:
                  rules:
                    - name: orders
                      priority: 10
                      match: { eq: [service, orders] }
                      team: {{team.TeamId}}
                      escalation_policy: {{escalation.PolicyId}}
                """), actor);
            await policies.ActivateAsync(PolicyKinds.Routing, routing.PolicyId, 1, actor);
            _integration = await sp.GetRequiredService<IntegrationService>().CreateAsync(new CreateIntegration("trend", "generic_webhook", "scope-a", team.TeamId), actor);
        });
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    private async Task<ProcessingResult> SendAsync(string eventType, string alertId, string severity)
    {
        var body = $$"""{"eventType":"{{eventType}}","alertId":"{{alertId}}","eventId":"{{Guid.NewGuid()}}","occurredAt":"{{_time.GetUtcNow():O}}","severity":"{{severity}}","environment":"production","service":"orders","resource":{"id":"res-{{alertId}}","name":"Orders"},"rule":{"id":"r-{{alertId}}","name":"disk trend"},"summary":"{{alertId}} {{eventType}} {{severity}}"}""";
        var accepted = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<IngestService>()
            .AcceptAsync(_integration.Integration, new IngestRequest(Encoding.UTF8.GetBytes(body), "application/json", new Dictionary<string, string>(), null)));
        var result = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<EventProcessor>()
            .ProcessAsync(new NormaliseJobPayload(accepted.EventId, accepted.ReceivedAt, _integration.Integration.IntegrationId)));
        await TestServices.InDbAsync(_services, db => db.Jobs.Where(j => j.Kind == JobKinds.Normalise && j.Status == JobStatus.Pending).ExecuteUpdateAsync(u => u.SetProperty(j => j.Status, JobStatus.Done)));
        return result;
    }

    private Task<Episode> EpisodeAsync(Guid id) => TestServices.InDbAsync(_services, db => db.Episodes.AsNoTracking().SingleAsync(e => e.EpisodeId == id));
    private Task<List<OutboxMessage>> OutboxAsync(Guid episodeId, string type) => TestServices.InDbAsync(_services, db => db.Outbox.AsNoTracking().Where(o => o.EpisodeId == episodeId && o.Type == type).OrderBy(o => o.CreatedAt).ToListAsync());
    private Task<int> AckDeadlineJobsAsync(Guid episodeId) => TestServices.InDbAsync(_services, db => db.Jobs.CountAsync(j => j.EpisodeId == episodeId && j.Kind == JobKinds.AckDeadline));

    [Fact]
    public async Task Scenario_LowSeverityEpisodeIsQueueOnly_owned_and_visible_but_no_notification_and_no_deadline()
    {
        var opened = await SendAsync("firing", "disk-low", "low");
        opened.Outcome.Should().Be(ProcessingOutcome.Opened, opened.Detail);
        var episode = await EpisodeAsync(opened.EpisodeId!.Value);
        episode.Severity.Should().Be(Severity.Low);
        episode.IsActionable.Should().BeTrue("a low episode is a real condition someone may pick up");
        episode.IsQueueOnly.Should().BeTrue();
        episode.OwningTeamId.Should().Be(_teamId, "routed and owned like any other episode");
        episode.AckDeadlineAt.Should().BeNull("queue only: no acknowledgement escalation (spec §9)");
        (await AckDeadlineJobsAsync(episode.EpisodeId)).Should().Be(0);
        (await OutboxAsync(episode.EpisodeId, NotificationTypes.EpisodeOpened)).Should().BeEmpty("queue only: nobody is told");
        (await TestServices.InDbAsync(_services, db => db.Outbox.AsNoTracking().Where(o => o.EpisodeId == episode.EpisodeId).Select(o => o.Type).ToListAsync())).Should().BeEmpty("queue only: no notification of any kind");

        _time.Advance(TimeSpan.FromMinutes(30));
        var resolved = await SendAsync("resolved", "disk-low", "low");
        resolved.Outcome.Should().Be(ProcessingOutcome.Resolved, resolved.Detail);
        (await OutboxAsync(episode.EpisodeId, NotificationTypes.EpisodeClosed)).Should().BeEmpty("no prior recipients, no closure notification");
    }

    [Fact]
    public async Task Scenario_LowEpisodeRisesToMedium_announced_as_a_new_actionable_episode_with_its_deadline_and_closure_reaches_those_recipients()
    {
        var opened = await SendAsync("firing", "disk-rising", "low");
        opened.Outcome.Should().Be(ProcessingOutcome.Opened, opened.Detail);
        var id = opened.EpisodeId!.Value;
        (await OutboxAsync(id, NotificationTypes.EpisodeOpened)).Should().BeEmpty();

        _time.Advance(TimeSpan.FromMinutes(5));
        var raised = await SendAsync("firing", "disk-rising", "medium");
        raised.Outcome.Should().Be(ProcessingOutcome.Updated, raised.Detail);
        raised.EpisodeId.Should().Be(id);
        var episode = await EpisodeAsync(id);
        episode.Severity.Should().Be(Severity.Medium);
        episode.IsQueueOnly.Should().BeFalse();
        episode.AckDeadlineAt.Should().Be(T0 + TimeSpan.FromMinutes(5) + TimeSpan.FromMinutes(15), "the deadline starts when the episode becomes actionable for people");
        (await AckDeadlineJobsAsync(id)).Should().Be(1);
        var openedRows = await OutboxAsync(id, NotificationTypes.EpisodeOpened);
        openedRows.Should().HaveCount(2, "the team hears about it now, as an opening, not as a severity change nobody heard the start of");
        openedRows.Should().OnlyContain(o => o.Payload.Contains("\"queueOnlyBefore\":true", StringComparison.Ordinal) || o.Payload.Contains("\"queueOnlyBefore\": true", StringComparison.Ordinal));
        (await OutboxAsync(id, NotificationTypes.EpisodeEscalatedSeverity)).Should().BeEmpty();

        _time.Advance(TimeSpan.FromMinutes(5));
        var critical = await SendAsync("firing", "disk-rising", "critical");
        critical.Outcome.Should().Be(ProcessingOutcome.Updated, critical.Detail);
        (await OutboxAsync(id, NotificationTypes.EpisodeEscalatedSeverity)).Should().HaveCount(2, "a material increase on an episode that already notified is a severity change");
        (await OutboxAsync(id, NotificationTypes.EpisodeOpened)).Should().HaveCount(2, "the opening is not repeated");

        _time.Advance(TimeSpan.FromMinutes(5));
        var resolved = await SendAsync("resolved", "disk-rising", "critical");
        resolved.Outcome.Should().Be(ProcessingOutcome.Resolved, resolved.Detail);
        (await OutboxAsync(id, NotificationTypes.EpisodeClosed)).Should().HaveCount(2, "prior recipients learn about the recovery");
    }
}
