using System.Text;
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
using AlertHub.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AlertHub.Integration.Tests.Milestone9;

/// <summary>Spec §22 <i>New critical child joins a group</i> and 04 §7.4: bounded windows, max severity, members keep their own lifecycle.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class GroupingScenarios(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    private string _cs = string.Empty;
    private FakeTimeProvider _time = null!;
    private ServiceProvider _services = null!;
    private IntegrationCredentials _integration = null!;

    private const string GroupingYaml = """
        grouping:
          rules:
            - name: by-service-and-environment
              match: { exists: "service" }
              key: [service, environment]
              window: 10m
              notify: first_and_new_critical
        """;

    public async Task InitializeAsync()
    {
        _cs = await postgres.CreateDatabaseAsync("grouping");
        _time = new FakeTimeProvider(T0);
        _services = TestServices.Build(_cs, _time, settings: new Dictionary<string, string?> { ["Notifications:AllowInsecureDestinations"] = "true" });
        var actor = Actor.System("setup");
        await TestServices.InScopeAsync(_services, async sp =>
        {
            var team = await sp.GetRequiredService<TeamService>().CreateAsync(new CreateTeam("mpt", ["scope-a"], IsTriage: true), actor);
            await sp.GetRequiredService<DestinationService>().CreatePairAsync(
                new CreateDestination("hook", ChannelTypes.Webhook, team.TeamId, null, Url: "http://receiver.test/a"),
                new CreateDestination("hook-fallback", ChannelTypes.Webhook, team.TeamId, null, Url: "http://receiver.test/b"), actor);
            var policies = sp.GetRequiredService<PolicyService>();
            var v = await policies.CreateVersionAsync(new CreatePolicyVersion(PolicyKinds.Grouping, GroupingYaml), actor);
            await policies.ActivateAsync(PolicyKinds.Grouping, v.PolicyId, v.Version, actor);
        });
        _integration = await TestServices.CreateIntegrationAsync(_services, "generic", "generic_webhook", "scope-a");
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    private async Task<ProcessingResult> OpenAsync(string alertId, string severity, string service = "orders", string environment = "production")
    {
        var body = $$"""{"eventType":"firing","alertId":"{{alertId}}","eventId":"{{Guid.NewGuid()}}","occurredAt":"{{_time.GetUtcNow():O}}","severity":"{{severity}}","environment":"{{environment}}","service":"{{service}}","resource":{"id":"res-{{alertId}}","name":"{{alertId}}"},"rule":{"id":"r-{{alertId}}","name":"rule"},"summary":"{{alertId}} {{severity}}"}""";
        var accepted = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<IngestService>()
            .AcceptAsync(_integration.Integration, new IngestRequest(Encoding.UTF8.GetBytes(body), "application/json", new Dictionary<string, string>(), null)));
        return await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<EventProcessor>().ProcessAsync(new NormaliseJobPayload(accepted.EventId, accepted.ReceivedAt, _integration.Integration.IntegrationId)));
    }

    private Task<int> OpenedNotificationsAsync(Guid episodeId)
        => TestServices.InDbAsync(_services, db => db.Outbox.CountAsync(o => o.EpisodeId == episodeId && o.Type == NotificationTypes.EpisodeOpened));

    [Fact]
    public async Task Scenario_NewCriticalChildJoinsAGroup_stays_visible_and_notifies_while_quiet_members_do_not()
    {
        var a = await OpenAsync("a", "medium");
        var b = await OpenAsync("b", "medium");
        var c = await OpenAsync("c", "critical");
        var other = await OpenAsync("d", "critical", service: "billing");

        await using var db = PostgresFixture.CreateContext(_cs);
        var groups = await db.AlertGroups.AsNoTracking().ToListAsync();
        groups.Should().HaveCount(2, "orders/production and billing/production are different keys");
        var group = groups.Single(g => g.KeyValues.Contains("orders", StringComparison.Ordinal));
        group.MemberCount.Should().Be(3);
        group.Severity.Should().Be("critical", "group severity is the max active member");
        group.WindowEndsAt.Should().Be(T0 + TimeSpan.FromMinutes(10));
        group.AccessScope.Should().Be("scope-a");

        var episodes = await db.Episodes.AsNoTracking().ToDictionaryAsync(e => e.EpisodeId);
        episodes[a.EpisodeId!.Value].GroupId.Should().Be(group.GroupId);
        episodes[b.EpisodeId!.Value].GroupId.Should().Be(group.GroupId);
        episodes[c.EpisodeId!.Value].GroupId.Should().Be(group.GroupId);
        episodes[other.EpisodeId!.Value].GroupId.Should().NotBe(group.GroupId);
        episodes.Values.Should().OnlyContain(e => e.HandlingState == HandlingState.New, "members keep their own handling and lifecycle");

        (await OpenedNotificationsAsync(a.EpisodeId!.Value)).Should().Be(2, "the first member notifies the team's two destinations");
        (await OpenedNotificationsAsync(b.EpisodeId!.Value)).Should().Be(0, "a quiet medium member joins silently");
        (await OpenedNotificationsAsync(c.EpisodeId!.Value)).Should().Be(2, "a new critical child stays visible and notifies on its own (spec §16.3)");
        var grouped = await db.EpisodeEvents.AsNoTracking().Where(e => e.Kind == EpisodeEventKind.Grouped).ToListAsync();
        grouped.Should().HaveCount(4);
        grouped.Single(e => e.EpisodeId == b.EpisodeId).Detail.Should().Contain("\"notify\": false");
    }

    [Fact]
    public async Task Window_closes_by_timer_and_the_next_member_starts_a_new_group()
    {
        var first = await OpenAsync("w1", "medium");
        _time.Advance(TimeSpan.FromMinutes(11));
        var claimed = await TestServices.InQueueAsync(_services, q => q.ClaimAsync([JobKinds.GroupWindowClose], "sched", TimeSpan.FromMinutes(1), 10));
        claimed.Should().ContainSingle();
        await TestServices.InScopeAsync(_services, async sp =>
        {
            var handler = sp.GetServices<IJobHandler>().Single(h => h.Kind == JobKinds.GroupWindowClose);
            await handler.HandleAsync(claimed[0], new JobContext("sched", (_, _) => Task.FromResult(true)), CancellationToken.None);
            await sp.GetRequiredService<IJobQueue>().CompleteAsync(claimed[0].JobId, "sched");
        });
        var second = await OpenAsync("w2", "medium");

        await using var db = PostgresFixture.CreateContext(_cs);
        var groups = await db.AlertGroups.AsNoTracking().OrderBy(g => g.OpenedAt).ToListAsync();
        groups.Should().HaveCount(2);
        groups[0].ClosedAt.Should().NotBeNull();
        groups[0].MemberCount.Should().Be(1);
        groups[1].ClosedAt.Should().BeNull();
        (await db.Episodes.AsNoTracking().SingleAsync(e => e.EpisodeId == second.EpisodeId)).GroupId.Should().Be(groups[1].GroupId);
        (await OpenedNotificationsAsync(second.EpisodeId!.Value)).Should().Be(2, "first member of the new window notifies again");
        (await db.Episodes.AsNoTracking().SingleAsync(e => e.EpisodeId == first.EpisodeId)).HandlingState.Should().Be(HandlingState.New, "closing the window closes nothing else");
    }
}
