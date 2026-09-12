using DeNoise.Application.Abstractions;
using DeNoise.Application.Auth;
using DeNoise.Application.Heartbeats;
using DeNoise.Application.Integrations;
using DeNoise.Application.Lifecycle;
using DeNoise.Application.Notifications;
using DeNoise.Application.Teams;
using DeNoise.Domain.Episodes;
using DeNoise.Domain.Heartbeats;
using DeNoise.Domain.Notifications;
using DeNoise.Domain.Ops;
using DeNoise.Domain.Users;
using DeNoise.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace DeNoise.Integration.Tests.Milestone6;

/// <summary>A maintenance calendar the test controls (the real one arrives with milestone 9).</summary>
public sealed class ScriptedMaintenance : IMaintenanceWindows
{
    public bool Covered { get; set; }
    public Task<bool> CoversAsync(string accessScope, DateTimeOffset now, CancellationToken ct = default) => Task.FromResult(Covered && accessScope == "scope-a");
}

/// <summary>
/// Spec §22 heartbeat scenarios: <i>misses its schedule</i>, <i>misses many intervals</i>, <i>calls /fail</i>, <i>token rotated</i>, <i>paused</i>,
/// <i>maintenance window covers a heartbeat's scope</i>, plus binding to integration coverage and YAML import/export. Real pings through the
/// service, real scheduler ticks, real database; time is a <see cref="FakeTimeProvider"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class HeartbeatScenarios(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    private string _cs = string.Empty;
    private FakeTimeProvider _time = null!;
    private ServiceProvider _services = null!;
    private readonly ScriptedMaintenance _maintenance = new();
    private Guid _team;
    private Guid _dest;
    private IntegrationCredentials _bound = null!;
    private DeNoisePrincipal _operator = null!;

    public async Task InitializeAsync()
    {
        _cs = await postgres.CreateDatabaseAsync("heartbeats");
        _time = new FakeTimeProvider(T0);
        _services = TestServices.Build(_cs, _time, s =>
        {
            s.RemoveAll<IMaintenanceWindows>();
            s.AddSingleton<IMaintenanceWindows>(_maintenance);
        }, new Dictionary<string, string?> { ["Notifications:AllowInsecureDestinations"] = "true", ["Heartbeats:IngestPublicBaseUrl"] = "https://ingest.test" });
        var actor = Actor.System("setup");
        await TestServices.InScopeAsync(_services, async sp =>
        {
            var team = await sp.GetRequiredService<TeamService>().CreateAsync(new CreateTeam("data-eng", ["scope-a"]), actor);
            _team = team.TeamId;
            await sp.GetRequiredService<TeamService>().CreateAsync(new CreateTeam("triage", ["scope-a"], IsTriage: true), actor);
            var (primary, _) = await sp.GetRequiredService<DestinationService>().CreatePairAsync(
                new CreateDestination("data-hook", ChannelTypes.Webhook, _team, null, Url: "http://receiver.test/data"),
                new CreateDestination("data-fallback", ChannelTypes.Webhook, _team, null, Url: "http://receiver.test/data-fallback"), actor);
            _dest = primary.Destination.DestinationId;
            _bound = await sp.GetRequiredService<IntegrationService>().CreateAsync(new CreateIntegration("poller", "generic_webhook", "scope-a", _team), actor);
        });
        _operator = DeNoisePrincipal.ForSession(new User { UserId = Guid.NewGuid(), Username = "olga", DisplayName = "Olga", RoleNames = [Roles.Operator], Scopes = ["scope-a"] }, "test-session");
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    private Task<HeartbeatCreated> CreateAsync(string name, int intervalMinutes = 5, int graceMinutes = 2, Guid? binds = null, int recovery = 1, string severity = "high")
        => TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<HeartbeatService>().CreateAsync(
            new HeartbeatDefinition(name, null, _team, null, ScheduleKinds.Interval, TimeSpan.FromMinutes(intervalMinutes), null, null, TimeSpan.FromMinutes(graceMinutes), severity, null, binds, recovery), _operator, "test"));

    private static string Credential(string pingUrl) => pingUrl[(pingUrl.IndexOf("/hb/", StringComparison.Ordinal) + 4)..];

    private Task<bool> PingAsync(string pingUrl, string kind = RunKinds.Success, int? exitCode = null, string? body = null)
        => TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<HeartbeatPingService>().PingAsync(Credential(pingUrl), kind, exitCode, body, System.Net.IPAddress.Loopback));

    private Task<IReadOnlyList<HeartbeatOutcome>> TickAsync() => TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<HeartbeatMonitor>().TickAsync(CancellationToken.None));

    private Task<Heartbeat> LoadAsync(Guid id) => TestServices.InDbAsync(_services, db => db.Heartbeats.AsNoTracking().SingleAsync(h => h.HeartbeatId == id));

    private Task<List<Episode>> MissEpisodesAsync(Guid heartbeatId) => TestServices.InDbAsync(_services,
        db => db.Episodes.AsNoTracking().Where(e => e.Fingerprint == HeartbeatEffects.Fingerprint(heartbeatId)).OrderBy(e => e.FirstSeen).ToListAsync());

    private Task<List<OutboxMessage>> OutboxAsync(Guid episodeId) => TestServices.InDbAsync(_services, db => db.Outbox.AsNoTracking().Where(o => o.EpisodeId == episodeId).OrderBy(o => o.CreatedAt).ToListAsync());

    private Task<List<HeartbeatRun>> RunsAsync(Guid id) => TestServices.InDbAsync(_services, db => db.HeartbeatRuns.AsNoTracking().Where(r => r.HeartbeatId == id).OrderBy(r => r.Seq).ToListAsync());

    [Fact]
    public async Task Scenario_RegisteredHeartbeatMissesItsSchedule_one_episode_owned_by_the_team_recovers_with_source_evidence()
    {
        var created = await CreateAsync("nightly-export");
        var hb = created.Heartbeat;
        created.PingUrl.Should().StartWith("https://ingest.test/hb/").And.Contain(hb.KeyId + ".");
        hb.State.Should().Be(HeartbeatStates.Healthy, "the deadline counts from registration");
        hb.ExpectedNext.Should().Be(T0.AddMinutes(5));

        _time.Advance(TimeSpan.FromMinutes(1));
        (await PingAsync(created.PingUrl)).Should().BeTrue();
        (await LoadAsync(hb.HeartbeatId)).ExpectedNext.Should().Be(T0.AddMinutes(6));

        _time.SetUtcNow(T0.AddMinutes(7));
        (await TickAsync()).Should().ContainSingle(o => o.Transition.To == HeartbeatStates.Late);
        (await MissEpisodesAsync(hb.HeartbeatId)).Should().BeEmpty("late is informational");

        _time.SetUtcNow(T0.AddMinutes(8).AddSeconds(1));
        (await TickAsync()).Should().ContainSingle(o => o.Transition.Missed);
        var episodes = await MissEpisodesAsync(hb.HeartbeatId);
        var miss = episodes.Should().ContainSingle().Which;
        miss.Severity.Should().Be(Domain.Common.Severity.High);
        miss.OwningTeamId.Should().Be(_team, "owned like any other alert, by the declared team");
        miss.RoutingCorrectionRequired.Should().BeFalse();
        miss.Summary.Should().Contain("nightly-export").And.Contain("missed");
        miss.LifecycleProfile.Should().Be(LifecycleProfiles.Heartbeat);
        miss.AutoResolveAt.Should().BeNull("miss episodes never resolve by inference");
        (await LoadAsync(hb.HeartbeatId)).MissEpisodeId.Should().Be(miss.EpisodeId);
        (await OutboxAsync(miss.EpisodeId)).Should().Contain(o => o.Type == NotificationTypes.HeartbeatMissed && o.DestinationId == _dest);
        await using (var db = PostgresFixture.CreateContext(_cs))
        {
            (await db.Jobs.CountAsync(j => j.EpisodeId == miss.EpisodeId && LifecycleScheduler.TimerKinds.Contains(j.Kind))).Should().Be(0);
        }

        _time.Advance(TimeSpan.FromMinutes(1));
        (await PingAsync(created.PingUrl)).Should().BeTrue();
        var recovered = await LoadAsync(hb.HeartbeatId);
        recovered.State.Should().Be(HeartbeatStates.Healthy);
        recovered.MissEpisodeId.Should().BeNull();
        var closed = (await MissEpisodesAsync(hb.HeartbeatId)).Single();
        closed.HandlingState.Should().Be(HandlingState.Closed);
        closed.ClosureReason.Should().Be(ClosureReason.SourceResolved);
        closed.ResolutionEvidence.Should().Be(Evidence.Source, "the job pinged: genuine confirmed recovery, not inference");
        (await OutboxAsync(miss.EpisodeId)).Select(o => o.Type).Should().Contain(NotificationTypes.HeartbeatRecovered).And.Contain(NotificationTypes.EpisodeClosed);
    }

    [Fact]
    public async Task Scenario_HeartbeatMissesManyIntervals_still_one_episode()
    {
        var created = await CreateAsync("hourly", intervalMinutes: 60, graceMinutes: 5);
        for (var hour = 1; hour <= 6; hour++)
        {
            _time.SetUtcNow(T0.AddHours(hour).AddMinutes(6));
            await TickAsync();
        }
        (await MissEpisodesAsync(created.Heartbeat.HeartbeatId)).Should().ContainSingle("not one per missed interval");
        (await LoadAsync(created.Heartbeat.HeartbeatId)).State.Should().Be(HeartbeatStates.Missed);
    }

    [Fact]
    public async Task Scenario_HeartbeatJobCallsFail_raises_immediately_and_keeps_the_output_tail()
    {
        var created = await CreateAsync("backup");
        _time.Advance(TimeSpan.FromMinutes(1));
        (await PingAsync(created.PingUrl, RunKinds.Start)).Should().BeTrue();
        _time.Advance(TimeSpan.FromSeconds(30));
        (await PingAsync(created.PingUrl, RunKinds.Fail, body: "pg_dump: error: connection to server failed\nexit 1")).Should().BeTrue();

        var hb = await LoadAsync(created.Heartbeat.HeartbeatId);
        hb.State.Should().Be(HeartbeatStates.Missed, "no waiting for the grace period");
        hb.LastRunDuration.Should().Be(TimeSpan.FromSeconds(30));
        var miss = (await MissEpisodesAsync(hb.HeartbeatId)).Should().ContainSingle().Which;
        miss.Summary.Should().Contain("/fail");
        var runs = await RunsAsync(hb.HeartbeatId);
        runs.Select(r => r.Kind).Should().Equal(RunKinds.Start, RunKinds.Fail);
        runs[1].Body.Should().Contain("pg_dump: error");

        _time.Advance(TimeSpan.FromMinutes(1));
        (await PingAsync(created.PingUrl, RunKinds.Exit, exitCode: 0)).Should().BeTrue();
        (await LoadAsync(hb.HeartbeatId)).State.Should().Be(HeartbeatStates.Healthy);
    }

    [Fact]
    public async Task Scenario_HeartbeatTokenIsRotated_old_token_stops_identity_and_history_stay()
    {
        var created = await CreateAsync("etl");
        _time.Advance(TimeSpan.FromMinutes(1));
        await PingAsync(created.PingUrl);
        var before = await LoadAsync(created.Heartbeat.HeartbeatId);

        var rotated = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<HeartbeatService>().RotateTokenAsync(before.HeartbeatId, before.Version, _operator, "test"));
        rotated.PingUrl.Should().NotBe(created.PingUrl);
        rotated.PingUrl.Should().Contain(before.KeyId + ".", "the key id (identity in the URL) is unchanged");

        (await PingAsync(created.PingUrl)).Should().BeFalse("old token");
        (await PingAsync(rotated.PingUrl)).Should().BeTrue();
        var after = await LoadAsync(before.HeartbeatId);
        after.HeartbeatId.Should().Be(before.HeartbeatId);
        after.State.Should().Be(HeartbeatStates.Healthy);
        (await RunsAsync(before.HeartbeatId)).Should().HaveCount(2, "history kept across rotation");
    }

    [Fact]
    public async Task Scenario_HeartbeatIsPaused_visibly_with_actor_and_reason_and_resume_causes_no_false_miss()
    {
        var created = await CreateAsync("reporting");
        _time.SetUtcNow(T0.AddMinutes(8));
        await TickAsync();
        var missed = await LoadAsync(created.Heartbeat.HeartbeatId);
        missed.State.Should().Be(HeartbeatStates.Missed);

        var paused = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<HeartbeatService>().PauseAsync(missed.HeartbeatId, missed.Version, "migration weekend", _operator, "test"));
        paused.State.Should().Be(HeartbeatStates.Paused);
        paused.PausedBy.Should().Be(_operator.UserId);
        paused.PauseReason.Should().Be("migration weekend");
        var miss = (await MissEpisodesAsync(missed.HeartbeatId)).Single();
        miss.HandlingState.Should().Be(HandlingState.Closed);
        miss.ClosureReason.Should().Be(ClosureReason.ManualClose, "pause closes the open miss as manual_close (04 §10)");

        _time.Advance(TimeSpan.FromDays(2));
        (await TickAsync()).Should().BeEmpty("paused heartbeats are not evaluated");
        (await MissEpisodesAsync(missed.HeartbeatId)).Should().ContainSingle();

        var current = await LoadAsync(missed.HeartbeatId);
        var resumed = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<HeartbeatService>().ResumeAsync(current.HeartbeatId, current.Version, _operator, "test"));
        resumed.State.Should().Be(HeartbeatStates.Unknown);
        resumed.ExpectedNext.Should().Be(_time.GetUtcNow().AddMinutes(5), "recomputed from now");

        _time.Advance(TimeSpan.FromMinutes(6));
        (await TickAsync()).Should().BeEmpty("unknown is not evaluated until the first ping; no false miss for the pause");
        await PingAsync(created.PingUrl);
        (await LoadAsync(missed.HeartbeatId)).State.Should().Be(HeartbeatStates.Healthy);
    }

    [Fact]
    public async Task Scenario_MaintenanceWindowCoversHeartbeatScope_auto_pauses_and_resumes_without_a_false_miss()
    {
        var created = await CreateAsync("cleanup");
        _time.Advance(TimeSpan.FromMinutes(1));
        await PingAsync(created.PingUrl);

        _maintenance.Covered = true;
        _time.Advance(TimeSpan.FromMinutes(1));
        (await TickAsync()).Should().ContainSingle(o => o.Transition.Paused);
        var paused = await LoadAsync(created.Heartbeat.HeartbeatId);
        paused.State.Should().Be(HeartbeatStates.Paused);
        paused.PausedByMaintenance.Should().BeTrue();
        paused.PausedBy.Should().BeNull();
        paused.PauseReason.Should().Be("maintenance window");

        _time.Advance(TimeSpan.FromHours(3));
        (await TickAsync()).Should().BeEmpty();
        _maintenance.Covered = false;
        (await TickAsync()).Should().ContainSingle(o => o.Transition.To == HeartbeatStates.Unknown);
        var resumed = await LoadAsync(created.Heartbeat.HeartbeatId);
        resumed.ExpectedNext.Should().Be(_time.GetUtcNow().AddMinutes(5));
        (await MissEpisodesAsync(created.Heartbeat.HeartbeatId)).Should().BeEmpty("no false miss across the window");

        _time.Advance(TimeSpan.FromMinutes(1));
        await PingAsync(created.PingUrl);
        (await LoadAsync(created.Heartbeat.HeartbeatId)).State.Should().Be(HeartbeatStates.Healthy);
    }

    [Fact]
    public async Task Bound_heartbeat_drives_the_integration_coverage_state()
    {
        var created = await CreateAsync("poller-hb", binds: _bound.Integration.IntegrationId, recovery: 2);
        _time.SetUtcNow(T0.AddMinutes(8));
        await TickAsync();
        await using (var db = PostgresFixture.CreateContext(_cs))
        {
            var coverage = await db.CoverageStates.SingleAsync(c => c.IntegrationId == _bound.Integration.IntegrationId);
            coverage.State.Should().Be(CoverageStates.Degraded, "a missed bound heartbeat is a coverage failure (spec §13.3.3)");
            (await db.Episodes.CountAsync(e => e.LifecycleProfile == LifecycleProfiles.Coverage && e.IntegrationId == _bound.Integration.IntegrationId)).Should().Be(1);
        }
        _time.Advance(TimeSpan.FromMinutes(1));
        await PingAsync(created.PingUrl);
        (await LoadAsync(created.Heartbeat.HeartbeatId)).State.Should().Be(HeartbeatStates.Missed, "recovery needs two pings");
        _time.Advance(TimeSpan.FromMinutes(1));
        await PingAsync(created.PingUrl);
        (await LoadAsync(created.Heartbeat.HeartbeatId)).State.Should().Be(HeartbeatStates.Healthy);
        await using (var db = PostgresFixture.CreateContext(_cs))
        {
            (await db.CoverageStates.SingleAsync(c => c.IntegrationId == _bound.Integration.IntegrationId)).State.Should().Be(CoverageStates.Healthy);
        }
    }

    [Fact]
    public async Task Yaml_export_and_import_round_trip_and_diff()
    {
        var a = await CreateAsync("job-a");
        await CreateAsync("job-b", intervalMinutes: 10);
        var yaml = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<HeartbeatService>().ExportAsync(_team, _operator));
        yaml.Should().Contain("name: \"job-a\"").And.Contain("interval: 10m").And.NotContain(a.PingUrl, "tokens are never exported");

        var dry = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<HeartbeatService>().ImportAsync(yaml, dryRun: true, _operator, "test"));
        dry.Errors.Should().BeEmpty();
        dry.Entries.Should().OnlyContain(e => e.Action == "unchanged");

        var changed = yaml.Replace("grace: 2m", "grace: 10m") + $"  - name: job-c\n    team: {_team}\n    schedule:\n      cron: \"30 2 * * *\"\n      timezone: Europe/Warsaw\n    grace: 15m\n    severity_on_miss: medium\n";
        var preview = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<HeartbeatService>().ImportAsync(changed, dryRun: true, _operator, "test"));
        preview.Entries.Should().Contain(e => e.Name == "job-a" && e.Action == "update" && e.Changes.Any(c => c.StartsWith("grace", StringComparison.Ordinal)));
        preview.Entries.Should().Contain(e => e.Name == "job-c" && e.Action == "create");
        (await LoadAsync(a.Heartbeat.HeartbeatId)).Grace.Should().Be(TimeSpan.FromMinutes(2), "dry run changes nothing");

        var applied = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<HeartbeatService>().ImportAsync(changed, dryRun: false, _operator, "test"));
        applied.Errors.Should().BeEmpty();
        applied.Entries.Single(e => e.Name == "job-c").PingUrl.Should().NotBeNull("shown once at creation");
        (await LoadAsync(a.Heartbeat.HeartbeatId)).Grace.Should().Be(TimeSpan.FromMinutes(10));
        var c = (await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<HeartbeatService>().ListAsync(new HeartbeatFilter(Query: "job-c")))).Single();
        c.ScheduleKind.Should().Be(ScheduleKinds.Cron);
        c.ScheduleTz.Should().Be("Europe/Warsaw");
        c.ExpectedNext.Should().NotBeNull();
    }
}
