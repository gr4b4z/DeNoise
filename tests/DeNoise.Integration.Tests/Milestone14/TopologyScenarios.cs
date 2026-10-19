using System.Text;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Ingest;
using DeNoise.Application.Integrations;
using DeNoise.Application.Mapping;
using DeNoise.Application.Notifications;
using DeNoise.Application.Ops;
using DeNoise.Application.Policies;
using DeNoise.Application.Processing;
using DeNoise.Application.Teams;
using DeNoise.Domain.Episodes;
using DeNoise.Domain.Notifications;
using DeNoise.Domain.Ops;
using DeNoise.Domain.Policies;
using DeNoise.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DeNoise.Integration.Tests.Milestone14;

/// <summary>
/// Spec §22 dependency rows (§16.5): <i>Host goes down with its services</i>, <i>Child fires before its parent</i>, <i>Parent closes while a
/// dependent still fires</i>, <i>No parent matches a dependency rule</i>, <i>Dependency policy declares a cycle</i>, <i>critical dependent
/// under a non-critical parent</i>. Real processing over a real database with a <see cref="FakeTimeProvider"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TopologyScenarios(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = PostgresFixture.TestEpoch;
    private string _cs = string.Empty;
    private FakeTimeProvider _time = null!;
    private ServiceProvider _services = null!;
    private IntegrationCredentials _integration = null!;
    private Guid _team;

    private const string TopologyYaml = """
        topology:
          rules:
            - name: host down suppresses its services
              parent: { match: { eq: [rule_id, host-down] }, key: [resource_id] }
              child:  { match: { neq: [rule_id, host-down] }, key: [labels.host] }
              window: 15m
              look_back: 2m
              action: mute_child
        """;

    public async Task InitializeAsync()
    {
        _cs = await postgres.CreateDatabaseAsync("topology");
        _time = new FakeTimeProvider(T0);
        _services = TestServices.Build(_cs, _time, settings: new Dictionary<string, string?> { ["Notifications:AllowInsecureDestinations"] = "true" });
        var actor = Actor.System("setup");
        await TestServices.InScopeAsync(_services, async sp =>
        {
            var team = await sp.GetRequiredService<TeamService>().CreateAsync(new CreateTeam("mpt", ["scope-a"], IsTriage: true), actor);
            _team = team.TeamId;
            await sp.GetRequiredService<DestinationService>().CreatePairAsync(
                new CreateDestination("hook", ChannelTypes.Webhook, team.TeamId, null, Url: "http://receiver.test/a"),
                new CreateDestination("hook-fallback", ChannelTypes.Webhook, team.TeamId, null, Url: "http://receiver.test/b"), actor);
            var policies = sp.GetRequiredService<PolicyService>();
            var v = await policies.CreateVersionAsync(new CreatePolicyVersion(PolicyKinds.Topology, TopologyYaml), actor);
            await policies.ActivateAsync(PolicyKinds.Topology, v.PolicyId, v.Version, actor);
        });
        _integration = await TestServices.CreateIntegrationAsync(_services, "infra", "generic_webhook", "scope-a");
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    // ---- helpers -------------------------------------------------------------------------------------------------

    private string Body(string eventType, string alertId, string ruleId, string resourceId, string severity, string? host)
        => $$"""{"eventType":"{{eventType}}","alertId":"{{alertId}}","eventId":"{{Guid.NewGuid()}}","occurredAt":"{{_time.GetUtcNow():O}}","severity":"{{severity}}","environment":"production","service":"{{ruleId}}","resource":{"id":"{{resourceId}}","name":"{{resourceId}}"},"rule":{"id":"{{ruleId}}","name":"{{ruleId}}"},"summary":"{{alertId}} {{eventType}}"{{(host is null ? "" : $",\"labels\":{{\"host\":\"{host}\"}}")}}}""";

    private async Task<ProcessingResult> SendAsync(string body)
    {
        var accepted = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<IngestService>()
            .AcceptAsync(_integration.Integration, new IngestRequest(Encoding.UTF8.GetBytes(body), "application/json", new Dictionary<string, string>(), null)));
        var result = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<EventProcessor>()
            .ProcessAsync(new NormaliseJobPayload(accepted.EventId, accepted.ReceivedAt, _integration.Integration.IntegrationId)));
        await TestServices.InDbAsync(_services, db => db.Jobs.Where(j => j.Kind == JobKinds.Normalise && j.Status == JobStatus.Pending).ExecuteUpdateAsync(u => u.SetProperty(j => j.Status, JobStatus.Done)));
        return result;
    }

    private async Task<Guid> HostDownAsync(string host, string severity = "high")
    {
        var r = await SendAsync(Body("firing", $"hd-{host}", "host-down", host, severity, null));
        r.Outcome.Should().Be(ProcessingOutcome.Opened, r.Detail);
        return r.EpisodeId!.Value;
    }

    private async Task<Guid> ServiceDownAsync(string service, string host, string severity = "medium")
    {
        var r = await SendAsync(Body("firing", $"svc-{service}", $"{service}-down", service, severity, host));
        r.Outcome.Should().Be(ProcessingOutcome.Opened, r.Detail);
        return r.EpisodeId!.Value;
    }

    private Task<Episode> EpisodeAsync(Guid id) => TestServices.InDbAsync(_services, db => db.Episodes.AsNoTracking().SingleAsync(e => e.EpisodeId == id));

    private Task<List<OutboxMessage>> OutboxAsync(Guid episodeId, string type) => TestServices.InDbAsync(_services,
        db => db.Outbox.AsNoTracking().Where(o => o.EpisodeId == episodeId && o.Type == type).OrderBy(o => o.CreatedAt).ToListAsync());

    private Task<List<EpisodeEvent>> TimelineAsync(Guid episodeId, string kind) => TestServices.InDbAsync(_services,
        db => db.EpisodeEvents.AsNoTracking().Where(e => e.EpisodeId == episodeId && e.Kind == kind).OrderBy(e => e.At).ToListAsync());

    private Task<List<Job>> JobsAsync(Guid episodeId, string kind) => TestServices.InDbAsync(_services,
        db => db.Jobs.AsNoTracking().Where(j => j.EpisodeId == episodeId && j.Kind == kind).OrderBy(j => j.CreatedAt).ToListAsync());

    private async Task RunJobsAsync(string kind)
    {
        var claimed = await TestServices.InQueueAsync(_services, q => q.ClaimAsync([kind], "sched", TimeSpan.FromMinutes(1), 10));
        foreach (var job in claimed)
        {
            await TestServices.InScopeAsync(_services, async sp =>
            {
                var handler = sp.GetServices<IJobHandler>().Single(h => h.Kind == kind);
                await handler.HandleAsync(job, new JobContext("sched", (_, _) => Task.FromResult(true)), CancellationToken.None);
                await sp.GetRequiredService<IJobQueue>().CompleteAsync(job.JobId, "sched");
            });
        }
    }

    // ---- scenarios -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Scenario_HostGoesDownWithItsServices_one_notification_for_the_parent_dependents_visible_owned_and_actionable()
    {
        var host = await HostDownAsync("host-1");
        (await OutboxAsync(host, NotificationTypes.EpisodeOpened)).Should().HaveCount(2).And.OnlyContain(o => o.Status == OutboxStatus.Pending, "the parent notifies normally");

        _time.Advance(TimeSpan.FromSeconds(20));
        var a = await ServiceDownAsync("api", "host-1");
        var b = await ServiceDownAsync("db", "host-1");
        var c = await ServiceDownAsync("cache", "host-1", "low");

        foreach (var id in new[] { a, b, c })
        {
            var child = await EpisodeAsync(id);
            child.DependsOnEpisodeId.Should().Be(host, "the child's labels.host equals the parent's resource_id");
            child.DependencyAction.Should().Be("mute_child");
            child.DependencyReleasedAt.Should().BeNull();
            child.DependencyWindowEndsAt.Should().Be(T0 + TimeSpan.FromMinutes(15), "the window is measured from the parent's opening");
            child.SuppressedUntil.Should().Be(T0 + TimeSpan.FromMinutes(15));
            child.SuppressionSource.Should().Be(SuppressionSources.Topology);
            child.IsOpen.Should().BeTrue("a dependent is a full episode");
            child.IsActionable.Should().BeTrue();
            child.OwningTeamId.Should().Be(_team, "owned like any other episode");
            child.HandlingState.Should().Be(HandlingState.New);
            (await OutboxAsync(id, NotificationTypes.EpisodeOpened)).Should().HaveCount(2).And.OnlyContain(o => o.Status == OutboxStatus.Suppressed, "only the notification is muted");
            var timeline = await TimelineAsync(id, EpisodeEventKind.Dependency);
            timeline.Should().ContainSingle().Which.Detail.Should().Contain("\"muted\": true").And.Contain("host down suppresses its services");
        }
        (await TimelineAsync(host, EpisodeEventKind.Dependency)).Should().HaveCount(3, "the parent's timeline shows every dependent attaching");
        (await JobsAsync(host, JobKinds.DependencyRelease)).Should().ContainSingle(j => j.Status == JobStatus.Pending && j.NotBefore == T0 + TimeSpan.FromMinutes(15), "one release timer per window end");
        var dependents = await TestServices.InDbAsync(_services, db => db.Episodes.AsNoTracking().CountAsync(e => e.DependsOnEpisodeId == host && e.DependencyReleasedAt == null));
        dependents.Should().Be(3);
        (await OutboxAsync(host, NotificationTypes.EpisodeOpened)).Should().HaveCount(2, "the parent is told once; the children add nothing");
    }

    [Fact]
    public async Task Scenario_ChildFiresBeforeItsParent_children_inside_the_look_back_are_adopted_and_dispatched_notifications_are_not_retracted()
    {
        var early = await ServiceDownAsync("queue", "host-2");
        var rows = await OutboxAsync(early, NotificationTypes.EpisodeOpened);
        rows.Should().HaveCount(2).And.OnlyContain(o => o.Status == OutboxStatus.Pending, "no parent exists yet: the child notifies normally");
        // One of the two rows was already delivered when the parent arrives.
        var sentId = rows[0].OutboxId;
        await TestServices.InDbAsync(_services, db => db.Outbox.Where(o => o.OutboxId == sentId).ExecuteUpdateAsync(u => u.SetProperty(o => o.Status, OutboxStatus.Sent).SetProperty(o => o.SentAt, _time.GetUtcNow())));

        _time.Advance(TimeSpan.FromMinutes(1));
        var host = await HostDownAsync("host-2");

        var child = await EpisodeAsync(early);
        child.DependsOnEpisodeId.Should().Be(host, "opened inside look_back (2m) before the parent: adopted");
        child.SuppressionSource.Should().Be(SuppressionSources.Topology);
        var after = await OutboxAsync(early, NotificationTypes.EpisodeOpened);
        after.Single(o => o.OutboxId == sentId).Status.Should().Be(OutboxStatus.Sent, "a dispatched notification is never retracted (spec §16.5)");
        after.Single(o => o.OutboxId != sentId).Status.Should().Be(OutboxStatus.Suppressed, "the undispatched one is muted");
        (await TimelineAsync(early, EpisodeEventKind.Dependency)).Select(e => e.Detail).Should().Contain(d => d!.Contains("\"adopted\": true", StringComparison.Ordinal));

        // A child that opened before the look-back is not adopted.
        _time.Advance(TimeSpan.FromMinutes(1));
        var old = await ServiceDownAsync("old", "host-7");
        _time.Advance(TimeSpan.FromMinutes(3));
        var lateParent = await HostDownAsync("host-7");
        (await EpisodeAsync(old)).DependsOnEpisodeId.Should().BeNull("3 minutes is outside look_back: 2m");
        (await EpisodeAsync(lateParent)).DependsOnEpisodeId.Should().BeNull();
    }

    [Fact]
    public async Task Scenario_ParentClosesWhileADependentStillFires_dependents_released_with_one_summary_per_team()
    {
        var host = await HostDownAsync("host-3");
        _time.Advance(TimeSpan.FromSeconds(10));
        var a = await ServiceDownAsync("web", "host-3");
        var b = await ServiceDownAsync("auth", "host-3");
        (await EpisodeAsync(a)).DependsOnEpisodeId.Should().Be(host);

        _time.Advance(TimeSpan.FromMinutes(2));
        var resolved = await SendAsync(Body("resolved", "hd-host-3", "host-down", "host-3", "high", null));
        resolved.Outcome.Should().Be(ProcessingOutcome.Resolved);

        foreach (var id in new[] { a, b })
        {
            var child = await EpisodeAsync(id);
            child.IsOpen.Should().BeTrue("closing the parent closes nothing else");
            child.DependsOnEpisodeId.Should().Be(host, "history keeps the relation");
            child.DependencyReleasedAt.Should().Be(_time.GetUtcNow());
            child.SuppressedUntil.Should().BeNull("released: a top-level actionable episode again");
            child.SuppressionSource.Should().BeNull();
            (await OutboxAsync(id, NotificationTypes.EpisodeOpened)).Should().OnlyContain(o => o.Status == OutboxStatus.Coalesced, "muted notifications are never replayed");
            (await TimelineAsync(id, EpisodeEventKind.Dependency)).Select(e => e.Detail).Should().Contain(d => d!.Contains("\"released\": true", StringComparison.Ordinal));
            (await TimelineAsync(id, EpisodeEventKind.SuppressionSummary)).Should().ContainSingle();
        }
        var summaries = await OutboxAsync(host, NotificationTypes.EpisodeSummaryAfterDependency);
        summaries.Should().HaveCount(2, "one current summary per team, to each of its two destinations");
        summaries[0].Payload.Should().Contain("\"count\": 2").And.Contain("svc-web").And.Contain("svc-auth");
        (await OutboxAsync(host, NotificationTypes.EpisodeClosed)).Should().HaveCount(2, "the parent's own closure notifies as usual");

        // A later child of the same host after the parent closed is not attributed to a closed parent.
        _time.Advance(TimeSpan.FromSeconds(5));
        var later = await ServiceDownAsync("later", "host-3");
        (await EpisodeAsync(later)).DependsOnEpisodeId.Should().BeNull("a dependency applies only while the parent is open");
    }

    [Fact]
    public async Task Window_end_releases_dependents_of_a_parent_that_is_still_open()
    {
        var host = await HostDownAsync("host-4");
        _time.Advance(TimeSpan.FromSeconds(10));
        var a = await ServiceDownAsync("metrics", "host-4");
        (await EpisodeAsync(a)).SuppressedUntil.Should().Be(T0 + TimeSpan.FromMinutes(15));

        _time.Advance(TimeSpan.FromMinutes(16));
        await RunJobsAsync(JobKinds.DependencyRelease);

        var child = await EpisodeAsync(a);
        child.DependencyReleasedAt.Should().NotBeNull("the window ended while the parent is still open");
        child.SuppressedUntil.Should().BeNull();
        (await EpisodeAsync(host)).IsOpen.Should().BeTrue();
        (await OutboxAsync(host, NotificationTypes.EpisodeSummaryAfterDependency)).Should().HaveCount(2);
        // A new child after the window is not attributed: the window is the bound of the relation.
        var late = await ServiceDownAsync("late", "host-4");
        (await EpisodeAsync(late)).DependsOnEpisodeId.Should().BeNull();
        (await OutboxAsync(late, NotificationTypes.EpisodeOpened)).Should().OnlyContain(o => o.Status == OutboxStatus.Pending);
    }

    [Fact]
    public async Task Scenario_NoParentMatchesADependencyRule_the_child_notifies_normally()
    {
        var orphan = await ServiceDownAsync("search", "host-9");
        var episode = await EpisodeAsync(orphan);
        episode.DependsOnEpisodeId.Should().BeNull();
        episode.SuppressedUntil.Should().BeNull();
        (await OutboxAsync(orphan, NotificationTypes.EpisodeOpened)).Should().HaveCount(2).And.OnlyContain(o => o.Status == OutboxStatus.Pending);
        (await TimelineAsync(orphan, EpisodeEventKind.Dependency)).Should().BeEmpty();
    }

    [Fact]
    public async Task Scenario_DependencyPolicyDeclaresACycle_refused_at_validation_with_the_offending_rule()
    {
        const string cyclic = """
            topology:
              rules:
                - name: db down suppresses app
                  parent: { match: { eq: [rule_id, db-down] }, key: [resource_id] }
                  child:  { match: { eq: [rule_id, app-down] }, key: [labels.db] }
                  action: mute_child
                - name: app down suppresses db
                  parent: { match: { eq: [rule_id, app-down] }, key: [resource_id] }
                  child:  { match: { eq: [rule_id, db-down] }, key: [labels.app] }
                  action: mute_child
            """;
        var act = () => TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<PolicyService>().CreateVersionAsync(new CreatePolicyVersion(PolicyKinds.Topology, cyclic), Actor.System("test")));
        var errors = (await act.Should().ThrowAsync<MappingValidationException>()).Which.Errors;
        errors.Should().ContainSingle().Which.Path.Should().Be("$.topology.rules[0]");
        errors[0].Message.Should().Contain("cycle").And.Contain("db down suppresses app").And.Contain("app down suppresses db");
        var versions = await TestServices.InDbAsync(_services, db => db.Policies.AsNoTracking().CountAsync(p => p.Kind == PolicyKinds.Topology));
        versions.Should().Be(1, "nothing was stored");
    }

    [Fact]
    public async Task Scenario_CriticalDependentUnderANonCriticalParent_notifies_on_its_own_unless_the_rule_names_critical()
    {
        var host = await HostDownAsync("host-5", "medium");
        _time.Advance(TimeSpan.FromSeconds(10));
        var critical = await ServiceDownAsync("payments", "host-5", "critical");
        var child = await EpisodeAsync(critical);
        child.DependsOnEpisodeId.Should().Be(host, "the relation is recorded");
        child.SuppressedUntil.Should().BeNull("critical is not in mute_severities");
        child.SuppressionSource.Should().BeNull();
        (await OutboxAsync(critical, NotificationTypes.EpisodeOpened)).Should().HaveCount(2).And.OnlyContain(o => o.Status == OutboxStatus.Pending, "the most severe class does not disappear behind a dependency by default");
        (await TimelineAsync(critical, EpisodeEventKind.Dependency)).Should().ContainSingle().Which.Detail.Should().Contain("\"muted\": false").And.Contain("notifies on its own");
    }
}
