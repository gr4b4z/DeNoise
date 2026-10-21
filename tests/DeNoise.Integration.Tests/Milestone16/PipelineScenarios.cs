using System.Text;
using System.Text.Json.Nodes;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Ingest;
using DeNoise.Application.Integrations;
using DeNoise.Application.Mapping;
using DeNoise.Application.Notifications;
using DeNoise.Application.Ops;
using DeNoise.Application.Policies;
using DeNoise.Application.Processing;
using DeNoise.Application.Teams;
using DeNoise.Domain.Common;
using DeNoise.Domain.Episodes;
using DeNoise.Domain.Integrations;
using DeNoise.Domain.Notifications;
using DeNoise.Domain.Ops;
using DeNoise.Domain.Policies;
using DeNoise.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DeNoise.Integration.Tests.Milestone16;

/// <summary>
/// Milestone 16 — pipeline sources (Azure DevOps Service Hooks, GitHub webhooks) through the seeded reference mappings and the
/// unchanged processing path: <i>Pipeline fails on main, then succeeds</i>, <i>Pipeline fails on a feature branch</i>, <i>Three jobs of
/// one run fail</i>, <i>Re-run of a failing build</i>, <i>Deployment fails in production, same release fails in staging</i>, <i>Late
/// completion of attempt 1 after attempt 2 resolved</i>, <i>Unknown Azure DevOps event is quarantined</i>. Real processing over a real
/// database; the clock is a <see cref="FakeTimeProvider"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PipelineScenarios(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = PostgresFixture.TestEpoch;
    private string _cs = string.Empty;
    private FakeTimeProvider _time = null!;
    private ServiceProvider _services = null!;
    private Guid _teamId;
    private IntegrationCredentials _ado = null!;
    private IntegrationCredentials _github = null!;

    public async Task InitializeAsync()
    {
        _cs = await postgres.CreateDatabaseAsync("pipelines");
        _time = new FakeTimeProvider(T0);
        _services = TestServices.Build(_cs, _time, settings: new Dictionary<string, string?> { ["Notifications:AllowInsecureDestinations"] = "true" });
        var actor = Actor.System("setup");
        await TestServices.InScopeAsync(_services, async sp =>
        {
            var team = await sp.GetRequiredService<TeamService>().CreateAsync(new CreateTeam("platform", ["scope-a"], IsTriage: true), actor);
            _teamId = team.TeamId;
            await sp.GetRequiredService<DestinationService>().CreatePairAsync(
                new CreateDestination("hook", ChannelTypes.Webhook, team.TeamId, null, Url: "http://receiver.test/a"),
                new CreateDestination("hook-fallback", ChannelTypes.Webhook, team.TeamId, null, Url: "http://receiver.test/b"), actor);
            var integrations = sp.GetRequiredService<IntegrationService>();
            var mappings = sp.GetRequiredService<MappingService>();
            _ado = await integrations.CreateAsync(new CreateIntegration("ado-orders", IntegrationTypes.AzureDevops, "scope-a", team.TeamId), actor);
            (await mappings.SeedReferenceAsync(_ado.Integration, actor)).Should().Be(4);
            _github = await integrations.CreateAsync(new CreateIntegration("github-octo", IntegrationTypes.Github, "scope-a", team.TeamId), actor);
            (await mappings.SeedReferenceAsync(_github.Integration, actor)).Should().Be(5);
        });
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static string Patch(string json, Action<JsonObject> edit)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        edit(node);
        return node.ToJsonString();
    }

    private static string Iso(DateTimeOffset at) => at.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static Dictionary<string, string> GithubHeaders(string eventName, string? delivery = null)
        => new(StringComparer.OrdinalIgnoreCase) { ["X-GitHub-Event"] = eventName, ["X-GitHub-Delivery"] = delivery ?? Guid.NewGuid().ToString() };

    private async Task<ProcessingResult> SendAsync(IntegrationCredentials integration, string body, IReadOnlyDictionary<string, string>? headers = null)
    {
        var accepted = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<IngestService>()
            .AcceptAsync(integration.Integration, new IngestRequest(Encoding.UTF8.GetBytes(body), "application/json", headers ?? new Dictionary<string, string>(), null)));
        var result = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<EventProcessor>()
            .ProcessAsync(new NormaliseJobPayload(accepted.EventId, accepted.ReceivedAt, integration.Integration.IntegrationId)));
        await TestServices.InDbAsync(_services, db => db.Jobs.Where(j => j.Kind == JobKinds.Normalise && j.Status == JobStatus.Pending && j.IntegrationId == integration.Integration.IntegrationId)
            .ExecuteUpdateAsync(u => u.SetProperty(j => j.Status, JobStatus.Done)));
        return result;
    }

    /// <summary>Azure DevOps <c>build.complete</c> for pipeline <c>orders-ci</c>, finished now.</summary>
    private string AdoBuild(string result, string branch = "refs/heads/main", string reason = "individualCI", int buildId = 4123)
        => Patch(ReferenceMappings.AzureDevopsBuildFailedSample, o =>
        {
            o["id"] = Guid.NewGuid().ToString();
            o["resource"]!["id"] = buildId;
            o["resource"]!["result"] = result;
            o["resource"]!["sourceBranch"] = branch;
            o["resource"]!["reason"] = reason;
            o["resource"]!["finishTime"] = Iso(_time.GetUtcNow());
            o["createdDate"] = Iso(_time.GetUtcNow().AddSeconds(1));
        });

    /// <summary>Azure DevOps <c>stage-state-changed</c> for pipeline <c>orders-cd</c>, the stage named after its environment.</summary>
    private string AdoStage(string stage, string result)
        => Patch(ReferenceMappings.AzureDevopsStageFailedSample, o =>
        {
            o["id"] = Guid.NewGuid().ToString();
            o["resource"]!["stage"]!["name"] = stage;
            o["resource"]!["stage"]!["displayName"] = stage;
            o["resource"]!["stage"]!["result"] = result;
            o["createdDate"] = Iso(_time.GetUtcNow());
        });

    /// <summary>GitHub <c>workflow_run</c> for <c>orders-ci</c> on <c>main</c>, updated at <paramref name="updatedAt"/> (default: now).</summary>
    private string GithubRun(string conclusion, int attempt = 1, DateTimeOffset? updatedAt = null, string branch = "main")
        => Patch(ReferenceMappings.GithubWorkflowRunFailureSample, o =>
        {
            o["workflow_run"]!["conclusion"] = conclusion;
            o["workflow_run"]!["run_attempt"] = attempt;
            o["workflow_run"]!["head_branch"] = branch;
            o["workflow_run"]!["updated_at"] = Iso(updatedAt ?? _time.GetUtcNow());
        });

    private string GithubJob(string job, long jobId)
        => Patch(ReferenceMappings.GithubWorkflowJobFailureSample, o =>
        {
            o["workflow_job"]!["id"] = jobId;
            o["workflow_job"]!["name"] = job;
            o["workflow_job"]!["completed_at"] = Iso(_time.GetUtcNow());
        });

    private Task<Episode> EpisodeAsync(Guid id) => TestServices.InDbAsync(_services, db => db.Episodes.AsNoTracking().SingleAsync(e => e.EpisodeId == id));

    private Task<List<OutboxMessage>> OutboxAsync(Guid episodeId, string type) => TestServices.InDbAsync(_services,
        db => db.Outbox.AsNoTracking().Where(o => o.EpisodeId == episodeId && o.Type == type).OrderBy(o => o.CreatedAt).ToListAsync());

    private Task ActivateGroupingAsync(string yaml) => TestServices.InScopeAsync(_services, async sp =>
    {
        var policies = sp.GetRequiredService<PolicyService>();
        var v = await policies.CreateVersionAsync(new CreatePolicyVersion(PolicyKinds.Grouping, yaml), Actor.System("setup"));
        await policies.ActivateAsync(PolicyKinds.Grouping, v.PolicyId, v.Version, Actor.System("setup"));
    });

    // ---- scenarios -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Scenario_PipelineFailsOnMainThenSucceeds_one_high_episode_owner_routed_resolved_by_the_source_and_prior_recipients_told()
    {
        var failed = await SendAsync(_ado, AdoBuild("failed"));
        failed.Outcome.Should().Be(ProcessingOutcome.Opened, failed.Detail);
        var id = failed.EpisodeId!.Value;
        var episode = await EpisodeAsync(id);
        episode.Severity.Should().Be(Severity.High, "main is a protected branch");
        episode.Environment.Should().Be("ci", "builds carry environment ci (M16 decision 2)");
        episode.RuleName.Should().Be("Build failed");
        episode.OwningTeamId.Should().Be(_teamId, "the integration's owning team routes the episode");
        episode.LifecycleProfile.Should().Be("explicit_recovery");
        episode.Summary.Should().Be("build orders-ci #20261021.3 failed (refs/heads/main)");
        (await OutboxAsync(id, NotificationTypes.EpisodeOpened)).Should().HaveCount(2, "the team's two destinations are told once");

        _time.Advance(TimeSpan.FromMinutes(12));
        var succeeded = await SendAsync(_ado, AdoBuild("succeeded", buildId: 4124));
        succeeded.Outcome.Should().Be(ProcessingOutcome.Resolved, succeeded.Detail);
        succeeded.EpisodeId.Should().Be(id, "the next run of the same pipeline on the same branch is the same condition");
        episode = await EpisodeAsync(id);
        episode.IsOpen.Should().BeFalse();
        episode.ClosureReason.Should().Be(ClosureReason.SourceResolved);
        episode.ResolutionEvidence.Should().Be(Evidence.Source);
        (await OutboxAsync(id, NotificationTypes.EpisodeClosed)).Should().HaveCount(2, "prior recipients learn about the recovery");
    }

    [Fact]
    public async Task Scenario_PipelineFailsOnAFeatureBranch_low_and_separate_from_main_and_pull_request_validation_is_informational_without_notification()
    {
        var main = await SendAsync(_ado, AdoBuild("failed", buildId: 5001));
        main.Outcome.Should().Be(ProcessingOutcome.Opened, main.Detail);

        var feature = await SendAsync(_ado, AdoBuild("failed", branch: "refs/heads/feature/totals", buildId: 5002));
        feature.Outcome.Should().Be(ProcessingOutcome.Opened, feature.Detail);
        feature.EpisodeId!.Value.Should().NotBe(main.EpisodeId!.Value, "the branch is part of a build's identity");
        var episode = await EpisodeAsync(feature.EpisodeId!.Value);
        episode.Severity.Should().Be(Severity.Low, "feature-branch failures are queue only (M16 decision 4)");
        episode.Environment.Should().Be("ci");
        episode.IsOpen.Should().BeTrue("it stays visible and searchable in the queue");

        var pr = await SendAsync(_ado, AdoBuild("failed", branch: "refs/pull/77/merge", reason: "pullRequest", buildId: 5003));
        pr.Outcome.Should().Be(ProcessingOutcome.Opened, pr.Detail);
        var validation = await EpisodeAsync(pr.EpisodeId!.Value);
        validation.Severity.Should().Be(Severity.Informational, "PR validation is informational");
        validation.IsActionable.Should().BeFalse();
        (await OutboxAsync(pr.EpisodeId!.Value, NotificationTypes.EpisodeOpened)).Should().BeEmpty("informational is queue only (spec §2.1)");
    }

    [Fact]
    public async Task Scenario_ThreeJobsOfOneRunFail_three_episodes_one_group_one_notification()
    {
        await ActivateGroupingAsync("""
            grouping:
              rules:
                - name: jobs of one run
                  match: { eq: [environment, ci] }
                  key: [resource_id, labels.run]
                  window: 5m
                  min_members: 2
                  notify: first_only
            """);

        var a = await SendAsync(_github, GithubJob("test (unit)", 7001), GithubHeaders("workflow_job"));
        _time.Advance(TimeSpan.FromSeconds(20));
        var b = await SendAsync(_github, GithubJob("test (integration)", 7002), GithubHeaders("workflow_job"));
        _time.Advance(TimeSpan.FromSeconds(20));
        var c = await SendAsync(_github, GithubJob("lint", 7003), GithubHeaders("workflow_job"));
        foreach (var r in new[] { a, b, c }) r.Outcome.Should().Be(ProcessingOutcome.Opened, r.Detail);
        new[] { a.EpisodeId, b.EpisodeId, c.EpisodeId }.Distinct().Should().HaveCount(3, "one episode per failing job");

        var groupIds = new List<Guid?>();
        foreach (var id in new[] { a.EpisodeId!.Value, b.EpisodeId!.Value, c.EpisodeId!.Value }) groupIds.Add((await EpisodeAsync(id)).GroupId);
        groupIds.Should().HaveCount(3).And.OnlyContain(g => g.HasValue);
        groupIds.Distinct().Should().ContainSingle("the three jobs of one run form one group");
        (await OutboxAsync(a.EpisodeId!.Value, NotificationTypes.EpisodeOpened)).Should().HaveCount(2, "the first member notifies");
        (await OutboxAsync(b.EpisodeId!.Value, NotificationTypes.EpisodeOpened)).Should().BeEmpty("first_only: later members are quiet");
        (await OutboxAsync(c.EpisodeId!.Value, NotificationTypes.EpisodeOpened)).Should().BeEmpty();
    }

    [Fact]
    public async Task Scenario_RerunOfAFailingBuild_same_episode_second_occurrence_no_second_notification_and_a_redelivery_is_a_duplicate()
    {
        var first = await SendAsync(_github, GithubRun("failure", attempt: 1), GithubHeaders("workflow_run", "d-100"));
        first.Outcome.Should().Be(ProcessingOutcome.Opened, first.Detail);
        var id = first.EpisodeId!.Value;

        _time.Advance(TimeSpan.FromMinutes(5));
        var rerun = await SendAsync(_github, GithubRun("failure", attempt: 2), GithubHeaders("workflow_run", "d-101"));
        rerun.Outcome.Should().Be(ProcessingOutcome.Updated, rerun.Detail);
        rerun.EpisodeId.Should().Be(id, "a re-run of the same workflow on the same branch is the same condition");
        (await EpisodeAsync(id)).OccurrenceCount.Should().Be(2);
        (await OutboxAsync(id, NotificationTypes.EpisodeOpened)).Should().HaveCount(2, "nobody is told twice");

        var redelivered = await SendAsync(_github, GithubRun("failure", attempt: 2), GithubHeaders("workflow_run", "d-101"));
        redelivered.Outcome.Should().Be(ProcessingOutcome.Duplicate, "X-GitHub-Delivery is the delivery key");
        (await EpisodeAsync(id)).OccurrenceCount.Should().Be(2);
    }

    [Fact]
    public async Task Scenario_DeploymentFailsInProductionAndStaging_two_episodes_by_environment_high_and_medium()
    {
        var production = await SendAsync(_ado, AdoStage("production", "failed"));
        production.Outcome.Should().Be(ProcessingOutcome.Opened, production.Detail);
        var staging = await SendAsync(_ado, AdoStage("staging", "failed"));
        staging.Outcome.Should().Be(ProcessingOutcome.Opened, staging.Detail);
        staging.EpisodeId!.Value.Should().NotBe(production.EpisodeId!.Value, "the target environment is part of a deployment's identity");

        var prod = await EpisodeAsync(production.EpisodeId!.Value);
        prod.Environment.Should().Be("production");
        prod.Severity.Should().Be(Severity.High, "a failed production deployment with an atomic rollback is high (M16 decision 1)");
        prod.RuleName.Should().Be("Deployment failed");
        var stg = await EpisodeAsync(staging.EpisodeId!.Value);
        stg.Environment.Should().Be("staging");
        stg.Severity.Should().Be(Severity.Medium);

        _time.Advance(TimeSpan.FromMinutes(3));
        var recovered = await SendAsync(_ado, AdoStage("production", "succeeded"));
        recovered.Outcome.Should().Be(ProcessingOutcome.Resolved, recovered.Detail);
        recovered.EpisodeId.Should().Be(production.EpisodeId);
        (await EpisodeAsync(staging.EpisodeId!.Value)).IsOpen.Should().BeTrue("staging is still red");
    }

    [Fact]
    public async Task Scenario_LateCompletionOfAttempt1AfterAttempt2Resolved_recorded_as_late_episode_stays_closed()
    {
        var failedAt = _time.GetUtcNow();
        var first = await SendAsync(_github, GithubRun("failure", attempt: 1, updatedAt: failedAt), GithubHeaders("workflow_run", "d-200"));
        first.Outcome.Should().Be(ProcessingOutcome.Opened, first.Detail);
        var id = first.EpisodeId!.Value;

        _time.Advance(TimeSpan.FromMinutes(10));
        var success = await SendAsync(_github, GithubRun("success", attempt: 2), GithubHeaders("workflow_run", "d-201"));
        success.Outcome.Should().Be(ProcessingOutcome.Resolved, success.Detail);

        _time.Advance(TimeSpan.FromMinutes(2));
        var late = await SendAsync(_github, GithubRun("failure", attempt: 1, updatedAt: failedAt.AddMinutes(4)), GithubHeaders("workflow_run", "d-202"));
        late.Outcome.Should().Be(ProcessingOutcome.Late, late.Detail);
        (await EpisodeAsync(id)).IsOpen.Should().BeFalse("a stale completion of the earlier attempt cannot reopen what attempt 2 resolved (04 §5)");
        (await TestServices.InDbAsync(_services, db => db.Episodes.AsNoTracking().CountAsync(e => e.IntegrationId == _github.Integration.IntegrationId && e.RuleName == "Build failed")))
            .Should().Be(1, "no second episode either");
    }

    [Fact]
    public async Task Scenario_UnknownAzureDevopsEvent_is_quarantined_with_the_integration_and_starts_open_nothing()
    {
        var unknown = await SendAsync(_ado, """{"id":"PROVISIONAL-ado-push-1","eventType":"git.push","publisherId":"tfs","resource":{"pushId":1},"resourceVersion":"1.0","createdDate":"2026-09-11T12:00:00Z"}""");
        unknown.Outcome.Should().Be(ProcessingOutcome.MappingFailed);
        var failures = await TestServices.InDbAsync(_services, db => db.MappingFailures.AsNoTracking().Where(f => f.IntegrationId == _ado.Integration.IntegrationId).ToListAsync());
        failures.Should().ContainSingle().Which.Error.Should().Contain("no active mapping applies", "a mis-subscribed hook is visible in the Failures tab, never silently dropped");

        var running = await SendAsync(_ado, Patch(ReferenceMappings.AzureDevopsRunFailedSample, o => { o["id"] = Guid.NewGuid().ToString(); o["resource"]!["run"]!["state"] = "inProgress"; o["resource"]!["run"]!.AsObject().Remove("result"); }));
        running.Outcome.Should().Be(ProcessingOutcome.RecordedWithoutEpisode, "a start is a fact, not a condition");
        running.EpisodeId.Should().BeNull();
    }
}
