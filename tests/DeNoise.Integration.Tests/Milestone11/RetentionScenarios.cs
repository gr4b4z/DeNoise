using System.Text;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Auth;
using DeNoise.Application.Episodes;
using DeNoise.Application.Ingest;
using DeNoise.Application.Integrations;
using DeNoise.Application.Processing;
using DeNoise.Application.Retention;
using DeNoise.Domain.Audit;
using DeNoise.Domain.Common;
using DeNoise.Domain.Episodes;
using DeNoise.Domain.Notifications;
using DeNoise.Domain.Users;
using DeNoise.Infrastructure.Persistence;
using DeNoise.Infrastructure.Retention;
using DeNoise.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DeNoise.Integration.Tests.Milestone11;

/// <summary>05 §7 retention jobs and the spec §22 scenario <em>Raw payload retention expires on an open episode</em>.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RetentionScenarios(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    private FakeTimeProvider _time = null!;
    private ServiceProvider _services = null!;
    private IntegrationCredentials _integration = null!;

    public async Task InitializeAsync()
    {
        var cs = await postgres.CreateDatabaseAsync("retention");
        _time = new FakeTimeProvider(T0);
        _services = TestServices.Build(cs, _time, settings: new Dictionary<string, string?> { ["Retention:BatchSize"] = "2" });
        _integration = await TestServices.CreateIntegrationAsync(_services, "gen", "generic_webhook", "scope-a");
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    private async Task<Guid> OpenEpisodeAsync(string alertId)
    {
        var body = $$"""{"eventType":"firing","alertId":"{{alertId}}","eventId":"{{Guid.NewGuid()}}","occurredAt":"{{_time.GetUtcNow():O}}","severity":"high","environment":"production","service":"orders","resource":{"id":"res-{{alertId}}","name":"Orders"},"rule":{"id":"5xx","name":"5xx rate"},"summary":"5xx for {{alertId}}"}""";
        return await TestServices.InScopeAsync(_services, async sp =>
        {
            var accepted = await sp.GetRequiredService<IngestService>().AcceptAsync(_integration.Integration, new IngestRequest(Encoding.UTF8.GetBytes(body), "application/json", new Dictionary<string, string>(), null));
            var result = await sp.GetRequiredService<EventProcessor>().ProcessAsync(new NormaliseJobPayload(accepted.EventId, accepted.ReceivedAt, _integration.Integration.IntegrationId));
            return result.EpisodeId ?? throw new InvalidOperationException(result.Outcome.ToString());
        });
    }

    [Fact]
    public async Task Scenario_RawPayloadRetentionExpiresOnAnOpenEpisode_EpisodeStaysActionableAndPayloadIsReportedGone()
    {
        var oldDay = DateOnly.FromDateTime(T0.UtcDateTime).AddDays(-40);
        var oldInstant = new DateTimeOffset(oldDay.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero);
        var episode = await OpenEpisodeAsync("old-open");
        await TestServices.InScopeAsync(_services, async sp =>
        {
            var db = sp.GetRequiredService<DeNoiseDbContext>();
            var partitions = sp.GetRequiredService<RawEventPartitions>();
            await partitions.EnsureDayAsync(oldDay);
            // Move the episode's raw payload into the 40-day-old partition, as if it had been received then.
            var eventId = await db.NormalisedEvents.Where(n => n.EpisodeId == episode).Select(n => n.EventId).SingleAsync();
            await db.Database.ExecuteSqlAsync($"INSERT INTO alert.raw_event (event_id, integration_id, received_at, content_type, body, headers, source_ip, size_bytes) SELECT event_id, integration_id, {oldInstant}, content_type, body, headers, source_ip, size_bytes FROM alert.raw_event WHERE event_id = {eventId}");
            await db.Database.ExecuteSqlAsync($"DELETE FROM alert.raw_event WHERE event_id = {eventId} AND received_at <> {oldInstant}");
            await db.Database.ExecuteSqlAsync($"UPDATE alert.normalised_event SET raw_received_at = {oldInstant} WHERE event_id = {eventId}");
        });
        var admin = DeNoisePrincipal.ForSession(new User { UserId = Guid.NewGuid(), Username = "admin", DisplayName = "Admin", RoleNames = [Roles.PlatformAdmin] }, "test");
        var before = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<IEpisodeQueries>().GetAsync(admin, episode));
        before!.RawPayloadAvailable.Should().BeTrue("the payload is still in a retained partition");

        var report = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<IRetentionRunner>().RunAsync());

        report.DroppedPartitions.Should().Equal(RawEventPartitions.PartitionName(oldDay));
        report.RawBoundary.Should().Be(new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero), "30 days before T0's day");
        var remaining = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<RawEventPartitions>().ListAsync());
        remaining.Should().NotContain(oldDay).And.Contain(DateOnly.FromDateTime(T0.UtcDateTime), "only partitions older than the boundary go; today and the days ahead stay");

        var after = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<IEpisodeQueries>().GetAsync(admin, episode));
        after!.RawPayloadAvailable.Should().BeFalse("the UI states the original payload is gone (05 §7)");
        after.Item.HandlingState.Should().NotBe("closed", "the episode stays actionable");
        after.Explanation.Should().NotBeNullOrEmpty();
        var eventId = await TestServices.InDbAsync(_services, db => db.NormalisedEvents.Where(n => n.EpisodeId == episode).Select(n => n.EventId).SingleAsync());
        var raw = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<IEpisodeQueries>().RawPayloadAsync(admin, episode, eventId));
        raw.Should().BeNull("the raw endpoint answers 404, the episode itself is untouched");

        // The drop and the run are audited.
        var audit = await TestServices.InDbAsync(_services, db => db.AuditEntries.Where(a => a.Action.StartsWith("retention.")).OrderBy(a => a.At).Select(a => a.Action).ToListAsync());
        audit.Should().Equal(RetentionService.DropAction, RetentionService.RunAction);
        var status = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<IRetentionRunner>().StatusAsync());
        status.LastRun!.DroppedPartitions.Should().HaveCount(1);
        status.NextRunAt.Should().Be(new DateTimeOffset(2026, 9, 12, 2, 0, 0, TimeSpan.Zero));
        // The fixture ensures partitions from the day before the test epoch (PostgresFixture.TestEpoch); the wall-clock partitions it also creates are always later.
        status.OldestPartition.Should().Be(DateOnly.FromDateTime(T0.UtcDateTime).AddDays(-1));
    }

    [Fact]
    public async Task Rows_are_deleted_by_age_in_batches_and_open_episodes_are_never_touched()
    {
        var open = await OpenEpisodeAsync("keep-open");
        var closedOld = await OpenEpisodeAsync("closed-old");
        var closedRecent = await OpenEpisodeAsync("closed-recent");
        var old = T0.AddMonths(-13);
        var ninetyOne = T0.AddDays(-91);
        await TestServices.InScopeAsync(_services, async sp =>
        {
            var db = sp.GetRequiredService<DeNoiseDbContext>();
            await db.Database.ExecuteSqlAsync($"UPDATE alert.episode SET handling_state = 'closed', closed_at = {old}, closure_reason = 'manual_close' WHERE episode_id = {closedOld}");
            await db.Database.ExecuteSqlAsync($"UPDATE alert.episode SET handling_state = 'closed', closed_at = {T0.AddDays(-1)}, closure_reason = 'manual_close' WHERE episode_id = {closedRecent}");
            // Every normalised event is "old": only the ones on closed episodes may go.
            await db.Database.ExecuteSqlAsync($"UPDATE alert.normalised_event SET received_at = {ninetyOne}");
            var now = T0;
            for (var i = 0; i < 5; i++)
            {
                db.AuditEntries.Add(new AuditEntry { Id = Ids.New(_time), At = old.AddDays(-i), ActorType = ActorTypes.System, ActorId = "x", Action = "test.old", TargetType = "t", TargetId = "1", CorrelationId = "c" });
                db.DeliveryAttempts.Add(new DeliveryAttempt { Id = Guid.NewGuid(), OutboxId = Guid.NewGuid(), AttemptedAt = now.AddDays(-31 - i), Channel = "webhook", Outcome = "success", LatencyMs = 1 });
                db.LoginAttempts.Add(new LoginAttempt { Id = Guid.NewGuid(), At = now.AddDays(-31 - i), Username = "someone", Success = false });
            }
            db.AuditEntries.Add(new AuditEntry { Id = Ids.New(_time), At = now.AddDays(-1), ActorType = ActorTypes.System, ActorId = "x", Action = "test.recent", TargetType = "t", TargetId = "1", CorrelationId = "c" });
            db.DeliveryAttempts.Add(new DeliveryAttempt { Id = Guid.NewGuid(), OutboxId = Guid.NewGuid(), AttemptedAt = now.AddDays(-1), Channel = "webhook", Outcome = "success", LatencyMs = 1 });
            db.LoginAttempts.Add(new LoginAttempt { Id = Guid.NewGuid(), At = now.AddDays(-1), Username = "someone", Success = true });
            await db.SaveChangesAsync();
        });
        var auditBefore = await TestServices.InDbAsync(_services, db => db.AuditEntries.CountAsync());

        var report = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<IRetentionRunner>().RunAsync());

        report.Deleted["episode"].Should().Be(1, "only the episode closed 13 months ago");
        report.Deleted["normalised_event"].Should().Be(2, "events of the two closed episodes; the open one keeps its history");
        report.Deleted["audit_entry"].Should().Be(5, "batched 2 + 2 + 1");
        report.Deleted["delivery_attempt"].Should().Be(5);
        report.Deleted["login_attempt"].Should().Be(5);
        report.DroppedPartitions.Should().BeEmpty();

        var episodes = await TestServices.InDbAsync(_services, db => db.Episodes.Select(e => e.EpisodeId).ToListAsync());
        episodes.Should().BeEquivalentTo([open, closedRecent]);
        (await TestServices.InDbAsync(_services, db => db.EpisodeEvents.CountAsync(e => e.EpisodeId == closedOld))).Should().Be(0, "the timeline cascades");
        (await TestServices.InDbAsync(_services, db => db.NormalisedEvents.CountAsync(n => n.EpisodeId == open))).Should().Be(1);
        (await TestServices.InDbAsync(_services, db => db.AuditEntries.CountAsync())).Should().Be(auditBefore - 5 + 1, "old entries gone, the run itself recorded");
        (await TestServices.InDbAsync(_services, db => db.DeliveryAttempts.CountAsync())).Should().Be(1);
        (await TestServices.InDbAsync(_services, db => db.LoginAttempts.CountAsync())).Should().Be(1);

        // Idempotent: a second run has nothing left to do.
        var again = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<IRetentionRunner>().RunAsync());
        again.TotalDeleted.Should().Be(0);
    }

    [Fact]
    public void Next_run_is_the_next_two_oclock_utc()
    {
        RetentionSchedule.NextRun(new DateTimeOffset(2026, 9, 11, 1, 59, 0, TimeSpan.Zero), 2).Should().Be(new DateTimeOffset(2026, 9, 11, 2, 0, 0, TimeSpan.Zero));
        RetentionSchedule.NextRun(new DateTimeOffset(2026, 9, 11, 2, 0, 0, TimeSpan.Zero), 2).Should().Be(new DateTimeOffset(2026, 9, 12, 2, 0, 0, TimeSpan.Zero));
        RetentionSchedule.NextRun(new DateTimeOffset(2026, 9, 11, 23, 30, 0, TimeSpan.FromHours(2)), 2).Should().Be(new DateTimeOffset(2026, 9, 12, 2, 0, 0, TimeSpan.Zero));
    }
}
