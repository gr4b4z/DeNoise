using System.Text;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Ingest;
using DeNoise.Application.Integrations;
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

namespace DeNoise.Integration.Tests.Milestone15;

/// <summary>
/// Spec §22 adaptive grouping rows (§16.3): <i>A group member arrives just after the window closed</i>, <i>Group members keep arriving</i>,
/// <i>A group forms after episodes of its key are already open</i>, <i>Group digest configured</i>, <i>A lone episode never becomes a group</i>;
/// plus the scalar-window regression. Each test activates its own grouping document (the kind is a single document).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AdaptiveGroupingScenarios(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = PostgresFixture.TestEpoch;
    private string _cs = string.Empty;
    private FakeTimeProvider _time = null!;
    private ServiceProvider _services = null!;
    private IntegrationCredentials _integration = null!;

    public async Task InitializeAsync()
    {
        _cs = await postgres.CreateDatabaseAsync("adaptive");
        _time = new FakeTimeProvider(T0);
        _services = TestServices.Build(_cs, _time, settings: new Dictionary<string, string?> { ["Notifications:AllowInsecureDestinations"] = "true" });
        var actor = Actor.System("setup");
        await TestServices.InScopeAsync(_services, async sp =>
        {
            var team = await sp.GetRequiredService<TeamService>().CreateAsync(new CreateTeam("mpt", ["scope-a"], IsTriage: true), actor);
            await sp.GetRequiredService<DestinationService>().CreatePairAsync(
                new CreateDestination("hook", ChannelTypes.Webhook, team.TeamId, null, Url: "http://receiver.test/a"),
                new CreateDestination("hook-fallback", ChannelTypes.Webhook, team.TeamId, null, Url: "http://receiver.test/b"), actor);
        });
        _integration = await TestServices.CreateIntegrationAsync(_services, "generic", "generic_webhook", "scope-a");
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    // ---- helpers -------------------------------------------------------------------------------------------------

    private Task ActivateAsync(string yaml) => TestServices.InScopeAsync(_services, async sp =>
    {
        var policies = sp.GetRequiredService<PolicyService>();
        var v = await policies.CreateVersionAsync(new CreatePolicyVersion(PolicyKinds.Grouping, yaml), Actor.System("setup"));
        await policies.ActivateAsync(PolicyKinds.Grouping, v.PolicyId, v.Version, Actor.System("setup"));
    });

    private static string Rule(string service, string window, string extra = "")
        => $"grouping:\n  rules:\n    - name: {service}-storm\n      match: {{ eq: [service, {service}] }}\n      key: [service, environment]\n      window: {window}\n{extra}";

    private async Task<Guid> OpenAsync(string alertId, string service, string severity = "medium")
    {
        var body = $$"""{"eventType":"firing","alertId":"{{alertId}}","eventId":"{{Guid.NewGuid()}}","occurredAt":"{{_time.GetUtcNow():O}}","severity":"{{severity}}","environment":"production","service":"{{service}}","resource":{"id":"res-{{alertId}}","name":"{{alertId}}"},"rule":{"id":"r-{{alertId}}","name":"rule"},"summary":"{{alertId}} {{severity}}"}""";
        var accepted = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<IngestService>()
            .AcceptAsync(_integration.Integration, new IngestRequest(Encoding.UTF8.GetBytes(body), "application/json", new Dictionary<string, string>(), null)));
        var result = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<EventProcessor>().ProcessAsync(new NormaliseJobPayload(accepted.EventId, accepted.ReceivedAt, _integration.Integration.IntegrationId)));
        await TestServices.InDbAsync(_services, db => db.Jobs.Where(j => j.Kind == JobKinds.Normalise && j.Status == JobStatus.Pending).ExecuteUpdateAsync(u => u.SetProperty(j => j.Status, JobStatus.Done)));
        result.Outcome.Should().Be(ProcessingOutcome.Opened, result.Detail);
        return result.EpisodeId!.Value;
    }

    private Task<Episode> EpisodeAsync(Guid id) => TestServices.InDbAsync(_services, db => db.Episodes.AsNoTracking().SingleAsync(e => e.EpisodeId == id));
    private Task<AlertGroup> GroupAsync(Guid id) => TestServices.InDbAsync(_services, db => db.AlertGroups.AsNoTracking().SingleAsync(g => g.GroupId == id));
    private async Task<List<AlertGroup>> GroupsAsync(string service)
        => (await TestServices.InDbAsync(_services, db => db.AlertGroups.AsNoTracking().OrderBy(g => g.OpenedAt).ToListAsync())).Where(g => g.KeyValues.Contains($"\"{service}\"", StringComparison.Ordinal)).ToList();
    private Task<List<OutboxMessage>> OutboxAsync(Guid episodeId, string type) => TestServices.InDbAsync(_services, db => db.Outbox.AsNoTracking().Where(o => o.EpisodeId == episodeId && o.Type == type).OrderBy(o => o.CreatedAt).ToListAsync());
    private Task<List<OutboxMessage>> HubOutboxAsync(string type) => TestServices.InDbAsync(_services, db => db.Outbox.AsNoTracking().Where(o => o.Type == type).OrderBy(o => o.CreatedAt).ToListAsync());
    private async Task<List<Job>> GroupJobsAsync(Guid groupId, string kind)
        => (await TestServices.InDbAsync(_services, db => db.Jobs.AsNoTracking().Where(j => j.Kind == kind).OrderBy(j => j.CreatedAt).ToListAsync())).Where(j => j.Payload.Contains(groupId.ToString(), StringComparison.OrdinalIgnoreCase)).ToList();

    private async Task<int> RunJobsAsync(string kind)
    {
        var claimed = await TestServices.InQueueAsync(_services, q => q.ClaimAsync([kind], "sched", TimeSpan.FromMinutes(1), 10));
        foreach (var job in claimed)
        {
            await TestServices.InScopeAsync(_services, async sp =>
            {
                var handler = sp.GetServices<IJobHandler>().Single(h => h.Kind == kind);
                var context = new JobContext("sched", (_, _) => Task.FromResult(true));
                await handler.HandleAsync(job, context, CancellationToken.None);
                if (!context.Retained) await sp.GetRequiredService<IJobQueue>().CompleteAsync(job.JobId, "sched");
            });
        }
        return claimed.Count;
    }

    // ---- scenarios -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Scenario_AGroupMemberArrivesJustAfterTheWindowClosed_joins_through_the_grace_and_no_second_group_starts()
    {
        await ActivateAsync(Rule("late", "{ initial: 1m, max: 1m, late_join: 2m }", "      notify: first_only\n"));
        var a = await OpenAsync("late-a", "late");
        _time.Advance(TimeSpan.FromSeconds(90));
        var b = await OpenAsync("late-b", "late", "critical");

        var groups = await GroupsAsync("late");
        groups.Should().ContainSingle("90 s is after the 1-minute window but inside the 2-minute grace");
        (await EpisodeAsync(b)).GroupId.Should().Be(groups[0].GroupId);
        groups[0].MemberCount.Should().Be(2);
        groups[0].WindowEndsAt.Should().Be(T0 + TimeSpan.FromMinutes(1), "no extension configured: the end does not move");
        groups[0].Severity.Should().Be("critical");
        (await OutboxAsync(b, NotificationTypes.EpisodeOpened)).Should().BeEmpty("first_only: the late joiner is a quiet member");
        (await OutboxAsync(a, NotificationTypes.EpisodeOpened)).Should().HaveCount(2);

        _time.Advance(TimeSpan.FromMinutes(2));
        var c = await OpenAsync("late-c", "late");
        groups = await GroupsAsync("late");
        groups.Should().HaveCount(2, "3.5 minutes is past the grace: a new group");
        groups[0].ClosedAt.Should().NotBeNull("the predecessor is closed in-line when its successor starts");
        groups[1].ContinuedFromGroupId.Should().Be(groups[0].GroupId);
        (await EpisodeAsync(c)).GroupId.Should().Be(groups[1].GroupId);
        (await OutboxAsync(c, NotificationTypes.EpisodeOpened)).Should().HaveCount(2, "first member of the continuation notifies");
    }

    [Fact]
    public async Task Scenario_GroupMembersKeepArriving_window_extends_to_the_maximum_then_closes_and_the_next_episode_continues_it()
    {
        await ActivateAsync(Rule("storm", "{ initial: 2m, extend_by: 2m, max: 5m }", "      notify: first_only\n"));
        var a = await OpenAsync("storm-a", "storm");
        var group = (await GroupsAsync("storm")).Single();
        group.WindowEndsAt.Should().Be(T0 + TimeSpan.FromMinutes(2));
        group.WindowHardEndAt.Should().Be(T0 + TimeSpan.FromMinutes(5));
        (await GroupJobsAsync(group.GroupId, JobKinds.GroupWindowClose)).Should().ContainSingle(j => j.NotBefore == T0 + TimeSpan.FromMinutes(2));

        _time.Advance(TimeSpan.FromMinutes(1));
        await OpenAsync("storm-b", "storm");
        group = await GroupAsync(group.GroupId);
        group.WindowEndsAt.Should().Be(T0 + TimeSpan.FromMinutes(4), "each member pushes the end out by extend_by");
        group.ExtensionCount.Should().Be(1);
        group.LastMemberAt.Should().Be(T0 + TimeSpan.FromMinutes(1));
        (await GroupJobsAsync(group.GroupId, JobKinds.GroupWindowClose)).Should().ContainSingle(j => j.Status == JobStatus.Pending && j.NotBefore == T0 + TimeSpan.FromMinutes(4), "the close timer follows the window");

        _time.Advance(TimeSpan.FromMinutes(2));
        await OpenAsync("storm-c", "storm");
        group = await GroupAsync(group.GroupId);
        group.WindowEndsAt.Should().Be(T0 + TimeSpan.FromMinutes(5), "capped at max measured from the first member");
        group.ExtensionCount.Should().Be(2);

        _time.Advance(TimeSpan.FromMinutes(1));
        await OpenAsync("storm-d", "storm");
        group = await GroupAsync(group.GroupId);
        group.WindowEndsAt.Should().Be(T0 + TimeSpan.FromMinutes(5), "the hard end never moves");
        group.ExtensionCount.Should().Be(2, "a member that cannot extend does not count as an extension");
        group.MemberCount.Should().Be(4);

        // The timer fires at the hard end even though members kept arriving.
        _time.Advance(TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(1)));
        (await RunJobsAsync(JobKinds.GroupWindowClose)).Should().Be(1);
        (await GroupAsync(group.GroupId)).ClosedAt.Should().NotBeNull("closed hard at max");

        var e = await OpenAsync("storm-e", "storm");
        var groups = await GroupsAsync("storm");
        groups.Should().HaveCount(2);
        groups[1].ContinuedFromGroupId.Should().Be(group.GroupId, "a storm is a chain of bounded groups");
        (await EpisodeAsync(e)).GroupId.Should().Be(groups[1].GroupId);
        (await OutboxAsync(e, NotificationTypes.EpisodeOpened)).Should().HaveCount(2, "the continuation is delivered: the maximum is what keeps a group from becoming a dropped alert");
        (await OutboxAsync(a, NotificationTypes.EpisodeOpened)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Scenario_AGroupFormsAfterEpisodesOfItsKeyAreAlreadyOpen_recent_episodes_are_adopted_without_duplicate_notification()
    {
        // Two episodes open before any grouping policy exists: each notified on its own.
        var a = await OpenAsync("retro-a", "retro");
        _time.Advance(TimeSpan.FromMinutes(1));
        var b = await OpenAsync("retro-b", "retro");
        (await GroupsAsync("retro")).Should().BeEmpty();
        // a's notification was already delivered; b's is still pending.
        var sent = (await OutboxAsync(a, NotificationTypes.EpisodeOpened))[0].OutboxId;
        await TestServices.InDbAsync(_services, db => db.Outbox.Where(o => o.OutboxId == sent).ExecuteUpdateAsync(u => u.SetProperty(o => o.Status, OutboxStatus.Sent)));

        await ActivateAsync(Rule("retro", "{ initial: 10m, max: 10m }", "      retro: { look_back: 5m }\n      notify: first_only\n"));
        _time.Advance(TimeSpan.FromMinutes(1));
        var c = await OpenAsync("retro-c", "retro");

        var group = (await GroupsAsync("retro")).Single();
        group.MemberCount.Should().Be(3);
        group.AdoptedCount.Should().Be(2, "both opened inside look_back");
        foreach (var id in new[] { a, b }) (await EpisodeAsync(id)).GroupId.Should().Be(group.GroupId);
        (await EpisodeAsync(c)).GroupId.Should().Be(group.GroupId);
        (await OutboxAsync(a, NotificationTypes.EpisodeOpened)).Select(o => o.Status).Should().Contain(OutboxStatus.Sent, "a dispatched notification is never retracted");
        (await OutboxAsync(b, NotificationTypes.EpisodeOpened)).Should().OnlyContain(o => o.Status == OutboxStatus.Coalesced, "an undispatched notification of an adopted member is cancelled");
        (await OutboxAsync(c, NotificationTypes.EpisodeOpened)).Should().BeEmpty("the key already has a delivered notification: no duplicate");
        var adoptedEntries = (await TestServices.InDbAsync(_services, db => db.EpisodeEvents.AsNoTracking().Where(e => e.Kind == EpisodeEventKind.Grouped).ToListAsync()))
            .Where(e => e.Detail!.Contains("\"adopted\": true", StringComparison.Ordinal)).ToList();
        adoptedEntries.Select(e => e.EpisodeId).Should().BeEquivalentTo([a, b], "adoption is on the timeline");

        // An episode opened before the look-back is left alone.
        _time.Advance(TimeSpan.FromMinutes(1));
        var old = await OpenAsync("retro2-old", "retro2");
        _time.Advance(TimeSpan.FromMinutes(6));
        await ActivateAsync(Rule("retro2", "{ initial: 10m, max: 10m }", "      retro: { look_back: 5m }\n"));
        await OpenAsync("retro2-new", "retro2");
        (await EpisodeAsync(old)).GroupId.Should().BeNull("7 minutes is outside look_back: 5m");
    }

    [Fact]
    public async Task Scenario_GroupDigestConfigured_one_digest_per_interval_capped_at_max_items()
    {
        await ActivateAsync(Rule("digest", "{ initial: 30m, max: 30m }", "      notify: digest\n      digest: { every: 5m, max_items: 2 }\n"));
        var a = await OpenAsync("dg-a", "digest");
        var group = (await GroupsAsync("digest")).Single();
        group.DigestNextAt.Should().Be(T0 + TimeSpan.FromMinutes(5));
        (await GroupJobsAsync(group.GroupId, JobKinds.GroupDigest)).Should().ContainSingle(j => j.NotBefore == T0 + TimeSpan.FromMinutes(5));
        (await OutboxAsync(a, NotificationTypes.EpisodeOpened)).Should().HaveCount(2, "the first member notifies at once");

        _time.Advance(TimeSpan.FromMinutes(1));
        var b = await OpenAsync("dg-b", "digest", "high");
        var c = await OpenAsync("dg-c", "digest");
        var d = await OpenAsync("dg-d", "digest", "critical");
        foreach (var id in new[] { b, c, d }) (await OutboxAsync(id, NotificationTypes.EpisodeOpened)).Should().BeEmpty("digest members are quiet until the roll-up");

        _time.Advance(TimeSpan.FromMinutes(4).Add(TimeSpan.FromSeconds(1)));
        (await RunJobsAsync(JobKinds.GroupDigest)).Should().Be(1);
        var digests = await HubOutboxAsync(NotificationTypes.EpisodeGroupDigest);
        digests.Should().HaveCount(2, "one digest to each of the team's two destinations");
        digests[0].Payload.Should().Contain("\"count\": 4").And.Contain("\"shown\": 2").And.Contain("\"more\": 2").And.Contain("dg-d").And.Contain("dg-b");
        digests[0].Payload.Should().NotContain("dg-c", "max_items: 2 keeps the two most severe");
        group = await GroupAsync(group.GroupId);
        group.DigestLastAt.Should().Be(_time.GetUtcNow());
        group.DigestNextAt.Should().Be(_time.GetUtcNow() + TimeSpan.FromMinutes(5), "re-armed while the group is open");
        (await GroupJobsAsync(group.GroupId, JobKinds.GroupDigest)).Should().ContainSingle(j => j.Status == JobStatus.Pending && j.NotBefore == group.DigestNextAt);

        // Next interval: only what joined since the previous digest.
        _time.Advance(TimeSpan.FromMinutes(2));
        var e = await OpenAsync("dg-e", "digest");
        _time.Advance(TimeSpan.FromMinutes(3).Add(TimeSpan.FromSeconds(1)));
        (await RunJobsAsync(JobKinds.GroupDigest)).Should().Be(1);
        digests = await HubOutboxAsync(NotificationTypes.EpisodeGroupDigest);
        digests.Should().HaveCount(4);
        digests[^1].Payload.Should().Contain("\"count\": 1").And.Contain("dg-e").And.NotContain("dg-a");
        (await TestServices.InDbAsync(_services, db => db.EpisodeEvents.AsNoTracking().CountAsync(ev => ev.EpisodeId == e && ev.Kind == EpisodeEventKind.Notify))).Should().Be(1);

        // Quiet interval with the group still open: no digest, timer re-armed.
        _time.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1)));
        (await RunJobsAsync(JobKinds.GroupDigest)).Should().Be(1);
        (await HubOutboxAsync(NotificationTypes.EpisodeGroupDigest)).Should().HaveCount(4, "nothing joined: no empty digest");
    }

    [Fact]
    public async Task Scenario_ALoneEpisodeNeverBecomesAGroup_notified_immediately_and_adopted_when_company_arrives()
    {
        await ActivateAsync(Rule("lone", "{ initial: 10m, max: 10m }", "      min_members: 2\n      notify: first_only\n"));
        var a = await OpenAsync("lone-a", "lone");
        (await GroupsAsync("lone")).Should().BeEmpty("below min_members no group exists");
        (await EpisodeAsync(a)).GroupId.Should().BeNull();
        (await OutboxAsync(a, NotificationTypes.EpisodeOpened)).Should().HaveCount(2).And.OnlyContain(o => o.Status == OutboxStatus.Pending, "notified immediately: grouping adds no latency");
        var grouped = await TestServices.InDbAsync(_services, db => db.EpisodeEvents.AsNoTracking().Where(e => e.EpisodeId == a && e.Kind == EpisodeEventKind.Grouped).ToListAsync());
        grouped.Should().ContainSingle().Which.Detail.Should().Contain("\"belowMinMembers\": true");

        _time.Advance(TimeSpan.FromSeconds(30));
        var b = await OpenAsync("lone-b", "lone");
        var group = (await GroupsAsync("lone")).Single();
        group.MemberCount.Should().Be(2);
        group.AdoptedCount.Should().Be(1, "the group materialises on the second member and adopts the first");
        (await EpisodeAsync(a)).GroupId.Should().Be(group.GroupId);
        (await EpisodeAsync(b)).GroupId.Should().Be(group.GroupId);
        (await OutboxAsync(a, NotificationTypes.EpisodeOpened)).Should().OnlyContain(o => o.Status == OutboxStatus.Pending, "the first member's notification stands for the key");
        (await OutboxAsync(b, NotificationTypes.EpisodeOpened)).Should().BeEmpty("no second notification for the same key");
    }

    [Fact]
    public async Task Scalar_window_keeps_the_fixed_window_behaviour()
    {
        await ActivateAsync(Rule("fixed", "10m", "      notify: first_and_new_critical\n"));
        var a = await OpenAsync("fixed-a", "fixed");
        var group = (await GroupsAsync("fixed")).Single();
        group.WindowEndsAt.Should().Be(T0 + TimeSpan.FromMinutes(10));
        group.WindowHardEndAt.Should().Be(T0 + TimeSpan.FromMinutes(10));
        _time.Advance(TimeSpan.FromMinutes(9));
        var b = await OpenAsync("fixed-b", "fixed", "critical");
        group = await GroupAsync(group.GroupId);
        group.WindowEndsAt.Should().Be(T0 + TimeSpan.FromMinutes(10), "no extension");
        group.ExtensionCount.Should().Be(0);
        (await OutboxAsync(b, NotificationTypes.EpisodeOpened)).Should().HaveCount(2, "a new critical child still notifies on its own");
        _time.Advance(TimeSpan.FromMinutes(2));
        var c = await OpenAsync("fixed-c", "fixed");
        (await GroupsAsync("fixed")).Should().HaveCount(2, "no grace: a second after the window a new group starts");
        (await EpisodeAsync(c)).GroupId.Should().NotBe(group.GroupId);
        (await EpisodeAsync(a)).GroupId.Should().Be(group.GroupId);
    }
}
