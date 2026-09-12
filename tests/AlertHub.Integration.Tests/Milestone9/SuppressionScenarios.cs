using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Heartbeats;
using AlertHub.Application.Ingest;
using AlertHub.Application.Integrations;
using AlertHub.Application.Notifications;
using AlertHub.Application.Ops;
using AlertHub.Application.Processing;
using AlertHub.Application.Suppressions;
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

/// <summary>
/// Spec §22 <i>Maintenance window ends with active condition</i> and <i>Maintenance crosses DST</i>: delivery is muted, never
/// ingestion or history; at the end each team gets one current summary and muted notifications are not replayed.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SuppressionScenarios(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    private string _cs = string.Empty;
    private FakeTimeProvider _time = null!;
    private ServiceProvider _services = null!;
    private IntegrationCredentials _integration = null!;
    private Guid _team;

    public async Task InitializeAsync()
    {
        _cs = await postgres.CreateDatabaseAsync("suppress");
        _time = new FakeTimeProvider(T0);
        _services = TestServices.Build(_cs, _time, settings: new Dictionary<string, string?> { ["Notifications:AllowInsecureDestinations"] = "true", ["Notifications:PublicBaseUrl"] = "https://hub.test" });
        var actor = Actor.System("setup");
        await TestServices.InScopeAsync(_services, async sp =>
        {
            var team = await sp.GetRequiredService<TeamService>().CreateAsync(new CreateTeam("mpt", ["scope-a"], IsTriage: true), actor);
            _team = team.TeamId;
            await sp.GetRequiredService<DestinationService>().CreatePairAsync(
                new CreateDestination("hook", ChannelTypes.Webhook, team.TeamId, null, Url: "http://receiver.test/a"),
                new CreateDestination("hook-fallback", ChannelTypes.Webhook, team.TeamId, null, Url: "http://receiver.test/b"), actor);
        });
        _integration = await TestServices.CreateIntegrationAsync(_services, "generic", "generic_webhook", "scope-a");
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    private async Task<Guid> OpenAsync(string alertId, string service = "orders")
    {
        var body = $$"""{"eventType":"firing","alertId":"{{alertId}}","eventId":"{{Guid.NewGuid()}}","occurredAt":"{{_time.GetUtcNow():O}}","severity":"high","environment":"production","service":"{{service}}","resource":{"id":"res-{{alertId}}","name":"{{alertId}}"},"rule":{"id":"r-{{alertId}}","name":"rule"},"summary":"{{alertId}} down"}""";
        var accepted = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<IngestService>()
            .AcceptAsync(_integration.Integration, new IngestRequest(Encoding.UTF8.GetBytes(body), "application/json", new Dictionary<string, string>(), null)));
        var result = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<EventProcessor>().ProcessAsync(new NormaliseJobPayload(accepted.EventId, accepted.ReceivedAt, _integration.Integration.IntegrationId)));
        result.Outcome.Should().Be(ProcessingOutcome.Opened, result.Detail);
        return result.EpisodeId!.Value;
    }

    private Task<Suppression> CreateAsync(CreateSuppression request)
        => TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<SuppressionService>().CreateAsync(request, Actor.System("ops")));

    private async Task RunDueSuppressionJobsAsync()
    {
        var claimed = await TestServices.InQueueAsync(_services, q => q.ClaimAsync([JobKinds.SuppressionEnd], "sched", TimeSpan.FromMinutes(1), 10));
        foreach (var job in claimed)
        {
            await TestServices.InScopeAsync(_services, async sp =>
            {
                var handler = sp.GetServices<IJobHandler>().Single(h => h.Kind == JobKinds.SuppressionEnd);
                await handler.HandleAsync(job, new JobContext("sched", (_, _) => Task.FromResult(true)), CancellationToken.None);
                await sp.GetRequiredService<IJobQueue>().CompleteAsync(job.JobId, "sched");
            });
        }
    }

    [Fact]
    public async Task Scenario_MaintenanceWindowEndsWithActiveCondition_current_summary_is_emitted_and_muted_notifications_are_not_replayed()
    {
        // An episode opened before the window: its opened notification is pending (the dispatcher has not run).
        var before = await OpenAsync("e1");
        var window = await CreateAsync(new CreateSuppression(SuppressionKinds.Maintenance, JsonNode.Parse("""{ "eq": ["service", "orders"] }""")!, "Europe/Warsaw", "database upgrade",
            StartsAt: _time.GetUtcNow(), EndsAt: _time.GetUtcNow() + TimeSpan.FromHours(2), Name: "orders-db-upgrade"));
        window.StartedAt.Should().NotBeNull("an already-started window mutes existing episodes at once");

        _time.Advance(TimeSpan.FromMinutes(5));
        var during = await OpenAsync("e2");
        var outside = await OpenAsync("e3", service: "billing");

        await using (var db = PostgresFixture.CreateContext(_cs))
        {
            var episodes = await db.Episodes.AsNoTracking().ToDictionaryAsync(e => e.EpisodeId);
            episodes[before].SuppressedUntil.Should().Be(window.EndsAt);
            episodes[before].SuppressionSource.Should().Be("maintenance");
            episodes[during].SuppressedUntil.Should().Be(window.EndsAt, "opened inside the window");
            episodes[outside].SuppressedUntil.Should().BeNull("billing is outside the scope predicate");
            (await db.Outbox.Where(o => o.EpisodeId == before).Select(o => o.Status).Distinct().ToListAsync()).Should().Equal([OutboxStatus.Suppressed], "pending rows were held back when the window began");
            (await db.Outbox.Where(o => o.EpisodeId == during).Select(o => o.Status).Distinct().ToListAsync()).Should().Equal([OutboxStatus.Suppressed]);
            (await db.Outbox.Where(o => o.EpisodeId == outside).Select(o => o.Status).Distinct().ToListAsync()).Should().Equal([OutboxStatus.Pending]);
            (await db.Episodes.CountAsync()).Should().Be(3, "suppression never blocks ingestion or episodes");
        }

        // The window ends while e1 and e2 are still firing.
        _time.Advance(TimeSpan.FromHours(2));
        await RunDueSuppressionJobsAsync();

        await using (var db = PostgresFixture.CreateContext(_cs))
        {
            var episodes = await db.Episodes.AsNoTracking().ToDictionaryAsync(e => e.EpisodeId);
            episodes[before].SuppressedUntil.Should().BeNull();
            episodes[during].SuppressedUntil.Should().BeNull();
            (await db.Outbox.Where(o => o.EpisodeId == before || o.EpisodeId == during).Select(o => o.Status).Distinct().ToListAsync()).Should().Equal([OutboxStatus.Coalesced], "muted notifications are never replayed (spec §16.3)");
            var summaries = await db.Outbox.AsNoTracking().Where(o => o.Type == NotificationTypes.EpisodeSummaryAfterSuppression).ToListAsync();
            summaries.Should().HaveCount(2, "one summary per team destination (the team has two)");
            summaries.Should().OnlyContain(o => o.Status == OutboxStatus.Pending && o.EpisodeId == null);
            var payload = JsonNode.Parse(summaries[0].Payload)!.AsObject();
            payload["event"]!.GetValue<string>().Should().Be(NotificationTypes.EpisodeSummaryAfterSuppression);
            payload["team"]!["id"]!.GetValue<string>().Should().Be(_team.ToString());
            var items = payload["episodes"]!.AsArray();
            items.Select(i => i!["id"]!.GetValue<string>()).Should().BeEquivalentTo([before.ToString(), during.ToString()], "the current actionable set, not the muted history");
            items.Should().OnlyContain(i => i!["url"]!.GetValue<string>().StartsWith("https://hub.test/episodes/", StringComparison.Ordinal));
            (await db.Suppressions.AsNoTracking().SingleAsync()).SummarySentAt.Should().NotBeNull();
            (await db.EpisodeEvents.CountAsync(e => e.Kind == EpisodeEventKind.SuppressionSummary)).Should().Be(2);
        }

        // Running the end again is a no-op (idempotent), and a new episode after the end is delivered normally.
        await RunDueSuppressionJobsAsync();
        var after = await OpenAsync("e4");
        await using (var db = PostgresFixture.CreateContext(_cs))
        {
            (await db.Outbox.CountAsync(o => o.Type == NotificationTypes.EpisodeSummaryAfterSuppression)).Should().Be(2);
            (await db.Outbox.Where(o => o.EpisodeId == after).Select(o => o.Status).Distinct().ToListAsync()).Should().Equal([OutboxStatus.Pending]);
        }
    }

    [Fact]
    public async Task Scenario_MaintenanceCrossesDst_window_declared_in_wall_clock_time_keeps_its_wall_clock_meaning()
    {
        // A future window schedules its start: existing episodes are muted when it begins, not before.
        var episode = await OpenAsync("dst-1");
        var future = await CreateAsync(new CreateSuppression(SuppressionKinds.Maintenance, JsonNode.Parse("""{ "eq": ["access_scope", "scope-a"] }""")!, "Europe/Warsaw", "evening patching",
            StartsAt: T0 + TimeSpan.FromHours(1), EndsAt: T0 + TimeSpan.FromHours(3)));
        future.StartedAt.Should().BeNull();
        await using (var db = PostgresFixture.CreateContext(_cs))
        {
            (await db.Episodes.AsNoTracking().SingleAsync(e => e.EpisodeId == episode)).SuppressedUntil.Should().BeNull("the window has not started");
            (await db.Jobs.CountAsync(j => j.Kind == JobKinds.SuppressionEnd && j.Status == JobStatus.Pending)).Should().Be(2, "start + end");
        }
        _time.Advance(TimeSpan.FromHours(1));
        await RunDueSuppressionJobsAsync();
        await using (var db = PostgresFixture.CreateContext(_cs))
        {
            var e = await db.Episodes.AsNoTracking().SingleAsync(x => x.EpisodeId == episode);
            e.SuppressedUntil.Should().Be(future.EndsAt);
            e.SuppressionSource.Should().Be("maintenance");
        }

        // Wall-clock windows on the two transition nights (Europe/Warsaw switches on 2027-03-28 and 2027-10-31).
        _time.SetUtcNow(new DateTimeOffset(2027, 3, 27, 12, 0, 0, TimeSpan.Zero));
        var spring = await CreateAsync(new CreateSuppression(SuppressionKinds.Maintenance, JsonNode.Parse("""{ "eq": ["access_scope", "scope-a"] }""")!, "Europe/Warsaw", "spring patching",
            StartsLocal: new DateTime(2027, 3, 28, 1, 30, 0), EndsLocal: new DateTime(2027, 3, 28, 3, 30, 0)));
        spring.StartsAt.Should().Be(new DateTimeOffset(2027, 3, 28, 0, 30, 0, TimeSpan.Zero));
        spring.EndsAt.Should().Be(new DateTimeOffset(2027, 3, 28, 1, 30, 0, TimeSpan.Zero), "one real hour: 02:00–03:00 did not happen");
        SuppressionText.Window(spring).Should().Be("2027-03-28 01:30 → 03:30 Europe/Warsaw");

        var autumn = await CreateAsync(new CreateSuppression(SuppressionKinds.Maintenance, JsonNode.Parse("""{ "eq": ["access_scope", "scope-a"] }""")!, "Europe/Warsaw", "autumn patching",
            StartsLocal: new DateTime(2027, 10, 31, 1, 30, 0), EndsLocal: new DateTime(2027, 10, 31, 3, 30, 0)));
        (autumn.EndsAt - autumn.StartsAt).Should().Be(TimeSpan.FromHours(3), "three real hours: 02:00–03:00 happened twice");

        // Heartbeats in the scope are covered exactly inside the window (both directions), never by the naive UTC reading.
        var windows = await TestServices.InScopeAsync(_services, async sp =>
        {
            var m = sp.GetRequiredService<IMaintenanceWindows>();
            return new[]
            {
                await m.CoversAsync("scope-a", new DateTimeOffset(2027, 3, 28, 0, 15, 0, TimeSpan.Zero)),
                await m.CoversAsync("scope-a", new DateTimeOffset(2027, 3, 28, 1, 0, 0, TimeSpan.Zero)),
                await m.CoversAsync("scope-a", new DateTimeOffset(2027, 3, 28, 1, 45, 0, TimeSpan.Zero)),
                await m.CoversAsync("scope-b", new DateTimeOffset(2027, 3, 28, 1, 0, 0, TimeSpan.Zero)),
                await m.CoversAsync("scope-a", new DateTimeOffset(2027, 10, 31, 2, 0, 0, TimeSpan.Zero)),
                await m.CoversAsync("scope-a", new DateTimeOffset(2027, 10, 31, 2, 45, 0, TimeSpan.Zero)),
            };
        });
        windows.Should().Equal(false, true, false, false, true, false);
    }

    [Fact]
    public async Task Cancelling_a_window_ends_it_now_with_the_same_consequences()
    {
        var window = await CreateAsync(new CreateSuppression(SuppressionKinds.Silence, JsonNode.Parse("""{ "eq": ["service", "orders"] }""")!, "UTC", "noisy deploy",
            StartsAt: _time.GetUtcNow(), EndsAt: _time.GetUtcNow() + TimeSpan.FromHours(4)));
        var episode = await OpenAsync("c1");
        var ended = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<SuppressionService>().CancelAsync(window.SuppressionId, Actor.System("ops")));
        ended.Should().NotBeNull();
        ended!.EpisodesReleased.Should().Be(1);
        ended.TeamsSummarised.Should().Equal(_team);
        await using var db = PostgresFixture.CreateContext(_cs);
        var s = await db.Suppressions.AsNoTracking().SingleAsync();
        s.CancelledAt.Should().NotBeNull();
        s.EndsAt.Should().Be(_time.GetUtcNow());
        (await db.Episodes.AsNoTracking().SingleAsync(e => e.EpisodeId == episode)).SuppressedUntil.Should().BeNull();
        (await db.AuditEntries.CountAsync(a => a.Action == "suppression.silence.cancel")).Should().Be(1);
        // Ending again (the scheduled end job) does nothing more.
        _time.Advance(TimeSpan.FromHours(5));
        await RunDueSuppressionJobsAsync();
        (await db.Outbox.CountAsync(o => o.Type == NotificationTypes.EpisodeSummaryAfterSuppression)).Should().Be(2);
    }
}
