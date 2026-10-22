using System.Text;
using System.Text.Json.Nodes;
using DeNoise.Application.Mapping;
using DeNoise.Domain.Alerts;
using DeNoise.Domain.Common;
using DeNoise.Domain.Integrations;

namespace DeNoise.Application.Tests.Mapping;

/// <summary>
/// Milestone 16 reference mappings for Azure DevOps Service Hooks and GitHub webhooks: a failure opens, the matching success resolves on
/// the same fingerprint, a start or a cancellation is recorded without an episode, kind / environment / severity follow the assumed
/// naming convention, and the ordered <c>applies_when</c> list picks one mapping per payload.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PipelineReferenceMappingsTests
{
    private static readonly Guid IntegrationId = Guid.Parse("00000000-0000-0000-0000-00000000c1c1");

    private static MappingResult Run(string yaml, string payload, IReadOnlyDictionary<string, string>? headers = null)
    {
        var doc = MappingParser.ParseYaml(yaml, 1);
        return MappingEngine.Normalise(doc, new RawInput(Encoding.UTF8.GetBytes(payload), headers ?? new Dictionary<string, string>()), new MappingContext(IntegrationId, Guid.NewGuid(), DateTimeOffset.UnixEpoch, 1));
    }

    private static string Patch(string json, Action<JsonObject> edit)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        edit(node);
        return node.ToJsonString();
    }

    private static IReadOnlyDictionary<string, string> GithubHeaders(string eventName, string delivery = "72d3162e-cc78-11e3-81ab-4c9367dc0958")
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-GitHub-Event"] = eventName, ["X-GitHub-Delivery"] = delivery };

    [Fact]
    public void Azure_devops_build_failure_opens_a_ci_build_episode_and_the_next_success_resolves_it()
    {
        var failed = Run(ReferenceMappings.AzureDevopsBuildYaml, ReferenceMappings.AzureDevopsBuildFailedSample);
        failed.Event.EventType.Should().Be(EventTypes.Firing);
        failed.Event.Severity.Should().Be(Severity.High, "refs/heads/main is a protected branch");
        failed.Event.Environment.Should().Be("ci", "builds carry environment ci (M16 decision 2)");
        failed.Event.RuleId.Should().Be("build-failed");
        failed.Event.ResourceName.Should().Be("orders-ci");
        failed.Event.Summary.Should().Be("build orders-ci #20261021.3 failed (refs/heads/main)");
        failed.Event.SourceAlertId.Should().Be("4123");
        failed.Event.SourceEventId.Should().Be("PROVISIONAL-ado-build-1");
        failed.Event.OccurredAt.Should().Be(new DateTimeOffset(2026, 10, 21, 10, 4, 12, TimeSpan.Zero), "the run's finish time orders the events (04 §5)");
        failed.Event.SourceVersion.Should().BeNull("run ids are not monotonic across re-runs; the finish time orders");
        failed.Event.IdentityComponents.Should().Contain(c => c.Name == "dims" && c.Value.Contains("refs/heads/main", StringComparison.Ordinal), "the branch is part of a build's identity");
        failed.Event.LifecycleProfileHint.Should().Be("explicit_recovery");

        var succeeded = Run(ReferenceMappings.AzureDevopsBuildYaml, ReferenceMappings.AzureDevopsBuildSucceededSample);
        succeeded.Event.EventType.Should().Be(EventTypes.Resolved);
        succeeded.Event.Fingerprint.Should().Be(failed.Event.Fingerprint, "a later run of the same pipeline on the same branch is the same condition");
        succeeded.Event.DeliveryKey.Should().NotBe(failed.Event.DeliveryKey);
    }

    [Fact]
    public void Branch_class_and_trigger_decide_the_severity_of_a_build_failure()
    {
        var feature = Run(ReferenceMappings.AzureDevopsBuildYaml, Patch(ReferenceMappings.AzureDevopsBuildFailedSample, o => o["resource"]!["sourceBranch"] = "refs/heads/feature/totals"));
        feature.Event.Severity.Should().Be(Severity.Low, "feature-branch failures are queue only (M16 decision 4)");
        feature.Event.Fingerprint.Should().NotBe(Run(ReferenceMappings.AzureDevopsBuildYaml, ReferenceMappings.AzureDevopsBuildFailedSample).Event.Fingerprint, "main and a feature branch never share an episode");

        var pr = Run(ReferenceMappings.AzureDevopsBuildYaml, Patch(ReferenceMappings.AzureDevopsBuildFailedSample, o => { o["resource"]!["reason"] = "pullRequest"; o["resource"]!["sourceBranch"] = "refs/pull/77/merge"; }));
        pr.Event.Severity.Should().Be(Severity.Informational, "PR validation is informational");

        var release = Run(ReferenceMappings.AzureDevopsBuildYaml, Patch(ReferenceMappings.AzureDevopsBuildFailedSample, o => o["resource"]!["sourceBranch"] = "refs/heads/release/2026.10"));
        release.Event.Severity.Should().Be(Severity.High);

        var classic = Run(ReferenceMappings.AzureDevopsBuildYaml, Patch(ReferenceMappings.AzureDevopsBuildFailedSample, o =>
        {
            o["resource"]!.AsObject().Remove("sourceBranch");
            o["resource"]!.AsObject().Remove("result");
            o["resource"]!["status"] = "failed";
            o["resource"]!["sourceGetVersion"] = "LG:refs/heads/master:9fceb02d";
        }));
        classic.Event.EventType.Should().Be(EventTypes.Firing, "the classic shape carries the result in status");
        classic.Event.Severity.Should().Be(Severity.High, "LG:refs/heads/master is the classic branch form");
    }

    [Fact]
    public void A_cancelled_or_stopped_run_is_recorded_without_an_episode()
    {
        var cancelled = Run(ReferenceMappings.AzureDevopsBuildYaml, Patch(ReferenceMappings.AzureDevopsBuildFailedSample, o => o["resource"]!["result"] = "canceled"));
        cancelled.Event.EventType.Should().Be(EventTypes.Informational, "a cancelled run proves nothing about the condition (conservative, AGENTS.md §5.11)");
        cancelled.Event.Severity.Should().Be(Severity.Informational);
        cancelled.Event.RuleId.Should().BeNull();
        cancelled.Event.Fingerprint.Should().BeNull("no rule, no identity, no episode (04 §2.1)");
    }

    [Fact]
    public void Pipeline_name_decides_kind_environment_and_severity_for_deploy_and_promote_pipelines()
    {
        var prodDeploy = Run(ReferenceMappings.AzureDevopsRunYaml, ReferenceMappings.AzureDevopsRunFailedSample);
        prodDeploy.Event.EventType.Should().Be(EventTypes.Firing);
        prodDeploy.Event.Environment.Should().Be("production", "orders-cd-prod names its target");
        prodDeploy.Event.Severity.Should().Be(Severity.High, "a failed production deployment with an atomic rollback is high (M16 decision 1)");
        prodDeploy.Event.RuleId.Should().Be("deploy-failed");
        prodDeploy.Event.IdentityComponents.Should().NotContain(c => c.Name == "dims" && c.Value.Contains("refs/heads/main", StringComparison.Ordinal), "the branch is not part of a deployment's identity");

        var staging = Run(ReferenceMappings.AzureDevopsRunYaml, Patch(ReferenceMappings.AzureDevopsRunFailedSample, o => { o["resource"]!["pipeline"]!["name"] = "orders-cd-staging"; o["resource"]!["run"]!["pipeline"]!["name"] = "orders-cd-staging"; }));
        staging.Event.Environment.Should().Be("staging");
        staging.Event.Severity.Should().Be(Severity.Medium);

        var promote = Run(ReferenceMappings.AzureDevopsRunYaml, Patch(ReferenceMappings.AzureDevopsRunFailedSample, o => { o["resource"]!["pipeline"]!["name"] = "promote-prod"; o["resource"]!["run"]!["pipeline"]!["name"] = "promote-prod"; }));
        promote.Event.Environment.Should().Be("production");
        promote.Event.RuleId.Should().Be("promotion-failed");
        promote.Event.Severity.Should().Be(Severity.High);

        var unknownName = Run(ReferenceMappings.AzureDevopsRunYaml, Patch(ReferenceMappings.AzureDevopsRunFailedSample, o => { o["resource"]!["pipeline"]!["name"] = "nightly"; o["resource"]!["run"]!["pipeline"]!["name"] = "nightly"; }));
        unknownName.Event.Environment.Should().Be("ci", "an unrecognised name falls back to the least noisy reading: a build");
        unknownName.Event.RuleId.Should().Be("build-failed");

        var running = Run(ReferenceMappings.AzureDevopsRunYaml, Patch(ReferenceMappings.AzureDevopsRunFailedSample, o => { o["resource"]!["run"]!["state"] = "inProgress"; o["resource"]!["run"]!.AsObject().Remove("result"); }));
        running.Event.EventType.Should().Be(EventTypes.Informational);
        running.Event.Fingerprint.Should().BeNull("a start opens nothing");
    }

    [Fact]
    public void Stage_named_after_the_environment_makes_one_episode_per_target()
    {
        var production = Run(ReferenceMappings.AzureDevopsStageYaml, ReferenceMappings.AzureDevopsStageFailedSample);
        production.Event.EventType.Should().Be(EventTypes.Firing);
        production.Event.Environment.Should().Be("production", "the stage name wins over the pipeline name");
        production.Event.Severity.Should().Be(Severity.High);
        production.Event.RuleId.Should().Be("deploy-failed");
        production.Event.SourceAlertId.Should().Be("9105:production");

        var staging = Run(ReferenceMappings.AzureDevopsStageYaml, Patch(ReferenceMappings.AzureDevopsStageFailedSample, o => { o["resource"]!["stage"]!["name"] = "staging"; o["resource"]!["stage"]!["displayName"] = "staging"; }));
        staging.Event.Environment.Should().Be("staging");
        staging.Event.Severity.Should().Be(Severity.Medium);
        staging.Event.Fingerprint.Should().NotBe(production.Event.Fingerprint, "two targets, two episodes");

        var buildStage = Run(ReferenceMappings.AzureDevopsStageYaml, Patch(ReferenceMappings.AzureDevopsStageFailedSample, o => { o["resource"]!["pipeline"]!["name"] = "orders-ci"; o["resource"]!["run"]!["pipeline"]!["name"] = "orders-ci"; o["resource"]!["stage"]!["name"] = "test"; }));
        buildStage.Event.Environment.Should().Be("ci");
        buildStage.Event.IdentityComponents.Should().Contain(c => c.Name == "dims" && c.Value.Contains("test", StringComparison.Ordinal) && c.Value.Contains("refs/heads/main", StringComparison.Ordinal));
    }

    [Fact]
    public void Classic_release_deployment_is_identified_by_release_definition_and_target_stage()
    {
        var failed = Run(ReferenceMappings.AzureDevopsReleaseYaml, ReferenceMappings.AzureDevopsReleaseFailedSample);
        failed.Event.EventType.Should().Be(EventTypes.Firing, "deploymentStatus failed");
        failed.Event.Environment.Should().Be("production");
        failed.Event.Severity.Should().Be(Severity.High);
        failed.Event.RuleId.Should().Be("deploy-failed");
        failed.Event.ResourceName.Should().Be("orders-release");
        failed.Event.OccurredAt.Should().Be(new DateTimeOffset(2026, 10, 21, 13, 12, 5, TimeSpan.Zero));
        failed.Event.IdentityComponents.Should().Contain(c => c.Name == "target" && c.Value == "production");

        var succeeded = Run(ReferenceMappings.AzureDevopsReleaseYaml, Patch(ReferenceMappings.AzureDevopsReleaseFailedSample, o => { o["resource"]!["deployment"]!["deploymentStatus"] = "succeeded"; o["resource"]!["environment"]!["status"] = "succeeded"; o["resource"]!["deployment"]!["id"] = 3013; }));
        succeeded.Event.EventType.Should().Be(EventTypes.Resolved);
        succeeded.Event.Fingerprint.Should().Be(failed.Event.Fingerprint, "the next release to the same stage recovers the condition, whatever its version");
    }

    [Fact]
    public void Github_workflow_run_failure_then_success_is_one_condition_and_the_delivery_id_is_the_delivery_key()
    {
        var failed = Run(ReferenceMappings.GithubWorkflowRunYaml, ReferenceMappings.GithubWorkflowRunFailureSample, GithubHeaders("workflow_run", "d-1"));
        failed.Event.EventType.Should().Be(EventTypes.Firing);
        failed.Event.Severity.Should().Be(Severity.High);
        failed.Event.Environment.Should().Be("ci");
        failed.Event.RuleId.Should().Be("build-failed");
        failed.Event.SourceEventId.Should().Be("d-1", "X-GitHub-Delivery is the idempotency key");
        failed.Event.DeliveryKey.Should().Be(Run(ReferenceMappings.GithubWorkflowRunYaml, ReferenceMappings.GithubWorkflowRunFailureSample, GithubHeaders("workflow_run", "d-1")).Event.DeliveryKey, "a redelivery is a duplicate");
        failed.Event.SourceAlertId.Should().Be("30433642");
        failed.Event.Summary.Should().Be("build octo-org/orders / orders-ci #562 failure (main)");
        failed.Event.SourceUrl.Should().Be("https://github.com/octo-org/orders/actions/runs/30433642");
        failed.Event.Labels.Should().Contain("event", "workflow_run");

        var success = Run(ReferenceMappings.GithubWorkflowRunYaml, ReferenceMappings.GithubWorkflowRunSuccessSample, GithubHeaders("workflow_run", "d-2"));
        success.Event.EventType.Should().Be(EventTypes.Resolved);
        success.Event.Fingerprint.Should().Be(failed.Event.Fingerprint);
        success.Event.OccurredAt.Should().BeAfter(failed.Event.OccurredAt!.Value);

        var withoutHeaders = Run(ReferenceMappings.GithubWorkflowRunYaml, ReferenceMappings.GithubWorkflowRunFailureSample);
        withoutHeaders.Event.SourceEventId.Should().Be("30433642:1:2026-10-21T10:05:00Z", "without the delivery header the run, attempt and update time identify the event");
    }

    [Fact]
    public void Github_starts_pull_request_runs_and_other_conclusions_stay_quiet()
    {
        var requested = Run(ReferenceMappings.GithubWorkflowRunYaml, Patch(ReferenceMappings.GithubWorkflowRunFailureSample, o => { o["action"] = "requested"; o["workflow_run"]!["status"] = "queued"; o["workflow_run"]!["conclusion"] = null; }));
        requested.Event.EventType.Should().Be(EventTypes.Informational);
        requested.Event.Fingerprint.Should().BeNull("GitHub sends requested and in_progress too; they open nothing");

        var pr = Run(ReferenceMappings.GithubWorkflowRunYaml, Patch(ReferenceMappings.GithubWorkflowRunFailureSample, o => { o["workflow_run"]!["event"] = "pull_request"; o["workflow_run"]!["head_branch"] = "feature/x"; }));
        pr.Event.EventType.Should().Be(EventTypes.Firing);
        pr.Event.Severity.Should().Be(Severity.Informational, "PR validation");

        var feature = Run(ReferenceMappings.GithubWorkflowRunYaml, Patch(ReferenceMappings.GithubWorkflowRunFailureSample, o => o["workflow_run"]!["head_branch"] = "feature/x"));
        feature.Event.Severity.Should().Be(Severity.Low);

        foreach (var conclusion in new[] { "cancelled", "skipped", "neutral", "stale", "action_required" })
        {
            var other = Run(ReferenceMappings.GithubWorkflowRunYaml, Patch(ReferenceMappings.GithubWorkflowRunFailureSample, o => o["workflow_run"]!["conclusion"] = conclusion));
            other.Event.EventType.Should().Be(EventTypes.Informational, $"{conclusion} says nothing about the build");
            other.Event.Fingerprint.Should().BeNull();
        }
        Run(ReferenceMappings.GithubWorkflowRunYaml, Patch(ReferenceMappings.GithubWorkflowRunFailureSample, o => o["workflow_run"]!["conclusion"] = "timed_out")).Event.EventType.Should().Be(EventTypes.Firing);
    }

    [Fact]
    public void Github_job_failures_are_one_episode_per_job_and_deploy_jobs_take_the_environment_from_the_job_name()
    {
        var unit = Run(ReferenceMappings.GithubWorkflowJobYaml, ReferenceMappings.GithubWorkflowJobFailureSample, GithubHeaders("workflow_job"));
        unit.Event.EventType.Should().Be(EventTypes.Firing);
        unit.Event.Environment.Should().Be("ci");
        unit.Event.Severity.Should().Be(Severity.High);
        unit.Event.RuleId.Should().Be("build-failed");
        unit.Event.Labels.Should().Contain("run", "30433642").And.Contain("job", "test (unit)");
        unit.Event.IdentityComponents.Should().Contain(c => c.Name == "dims" && c.Value.Contains("test (unit)", StringComparison.Ordinal));

        var lint = Run(ReferenceMappings.GithubWorkflowJobYaml, Patch(ReferenceMappings.GithubWorkflowJobFailureSample, o => o["workflow_job"]!["name"] = "lint"));
        lint.Event.Fingerprint.Should().NotBe(unit.Event.Fingerprint);

        var deployJob = Run(ReferenceMappings.GithubWorkflowJobYaml, Patch(ReferenceMappings.GithubWorkflowJobFailureSample, o => { o["workflow_job"]!["workflow_name"] = "orders-cd"; o["workflow_job"]!["name"] = "deploy staging"; }));
        deployJob.Event.Environment.Should().Be("staging");
        deployJob.Event.Severity.Should().Be(Severity.Medium);
        deployJob.Event.RuleId.Should().Be("deploy-failed");
    }

    [Fact]
    public void Github_deployment_status_is_a_deployment_condition_per_repository_and_environment()
    {
        var failed = Run(ReferenceMappings.GithubDeploymentStatusYaml, ReferenceMappings.GithubDeploymentStatusFailureSample, GithubHeaders("deployment_status"));
        failed.Event.EventType.Should().Be(EventTypes.Firing);
        failed.Event.Environment.Should().Be("production");
        failed.Event.Severity.Should().Be(Severity.High);
        failed.Event.RuleId.Should().Be("deploy-failed");
        failed.Event.Summary.Should().Be("deploy octo-org/orders to production: failure (helm upgrade --atomic rolled back)");
        failed.Event.Labels.Should().Contain("pipeline", "orders-cd");

        var error = Run(ReferenceMappings.GithubDeploymentStatusYaml, Patch(ReferenceMappings.GithubDeploymentStatusFailureSample, o => o["deployment_status"]!["state"] = "error"));
        error.Event.EventType.Should().Be(EventTypes.Firing);
        error.Event.Fingerprint.Should().Be(failed.Event.Fingerprint);

        var success = Run(ReferenceMappings.GithubDeploymentStatusYaml, Patch(ReferenceMappings.GithubDeploymentStatusFailureSample, o => { o["deployment_status"]!["state"] = "success"; o["deployment"]!["id"] = 145988800; }));
        success.Event.EventType.Should().Be(EventTypes.Resolved);
        success.Event.Fingerprint.Should().Be(failed.Event.Fingerprint, "the next deployment to the same environment recovers it");

        foreach (var state in new[] { "pending", "queued", "in_progress", "waiting", "inactive" })
        {
            var transient = Run(ReferenceMappings.GithubDeploymentStatusYaml, Patch(ReferenceMappings.GithubDeploymentStatusFailureSample, o => o["deployment_status"]!["state"] = state));
            transient.Event.EventType.Should().Be(EventTypes.Informational, state);
            transient.Event.Fingerprint.Should().BeNull(state);
        }

        var preview = Run(ReferenceMappings.GithubDeploymentStatusYaml, Patch(ReferenceMappings.GithubDeploymentStatusFailureSample, o => { o["deployment"]!["environment"] = "pr-123"; o["deployment_status"]!["environment"] = "pr-123"; }));
        preview.Event.Environment.Should().Be("ephemeral");
        preview.Event.Severity.Should().Be(Severity.Low);
        preview.Event.Fingerprint.Should().NotBe(failed.Event.Fingerprint);
    }

    [Fact]
    public void Github_deployment_created_and_ping_are_recorded_without_an_episode()
    {
        var created = Run(ReferenceMappings.GithubDeploymentYaml, ReferenceMappings.GithubDeploymentCreatedSample, GithubHeaders("deployment"));
        created.Event.EventType.Should().Be(EventTypes.Informational);
        created.Event.Fingerprint.Should().BeNull();
        created.Event.Summary.Should().Be("deployment of octo-org/orders to production requested (main)");

        var ping = Run(ReferenceMappings.GithubPingYaml, ReferenceMappings.GithubPingSample, GithubHeaders("ping"));
        ping.Event.EventType.Should().Be(EventTypes.Informational);
        ping.Event.Fingerprint.Should().BeNull();
        ping.Event.Summary.Should().StartWith("GitHub webhook ping");
    }

    [Fact]
    public void The_ordered_applies_when_list_selects_exactly_one_mapping_per_payload()
    {
        var github = ReferenceMappings.For(IntegrationTypes.Github).Select(r => (r.Name, Doc: MappingParser.ParseYaml(r.Yaml, 1))).ToList();
        var cases = new (string Payload, string Header, string Expected)[]
        {
            (ReferenceMappings.GithubPingSample, "ping", "github-ping"),
            (ReferenceMappings.GithubDeploymentStatusFailureSample, "deployment_status", "github-deployment-status"),
            (ReferenceMappings.GithubDeploymentCreatedSample, "deployment", "github-deployment"),
            (ReferenceMappings.GithubWorkflowJobFailureSample, "workflow_job", "github-workflow-job"),
            (ReferenceMappings.GithubWorkflowRunFailureSample, "workflow_run", "github-workflow-run"),
        };
        foreach (var (payload, header, expected) in cases)
        {
            var input = new RawInput(Encoding.UTF8.GetBytes(payload), GithubHeaders(header));
            var body = input.ParseBody();
            var headers = input.HeadersAsJson();
            github.Where(m => MappingEngine.AppliesTo(m.Doc, body, headers)).Select(m => m.Name).Should().ContainSingle().Which.Should().Be(expected);
        }

        var ado = ReferenceMappings.For(IntegrationTypes.AzureDevops).Select(r => (r.Name, Doc: MappingParser.ParseYaml(r.Yaml, 1))).ToList();
        var adoCases = new (string Payload, string Expected)[]
        {
            (ReferenceMappings.AzureDevopsBuildFailedSample, "azure-devops-build"),
            (ReferenceMappings.AzureDevopsRunFailedSample, "azure-devops-yaml-run"),
            (ReferenceMappings.AzureDevopsStageFailedSample, "azure-devops-yaml-stage"),
            (ReferenceMappings.AzureDevopsReleaseFailedSample, "azure-devops-release-deployment"),
        };
        foreach (var (payload, expected) in adoCases)
        {
            var input = new RawInput(Encoding.UTF8.GetBytes(payload), new Dictionary<string, string>());
            ado.Where(m => MappingEngine.AppliesTo(m.Doc, input.ParseBody(), input.HeadersAsJson())).Select(m => m.Name).Should().ContainSingle().Which.Should().Be(expected);
        }
        var unknown = new RawInput(Encoding.UTF8.GetBytes("""{"id":"x","eventType":"git.push","resource":{}}"""), new Dictionary<string, string>());
        ado.Should().NotContain(m => MappingEngine.AppliesTo(m.Doc, unknown.ParseBody(), unknown.HeadersAsJson()), "an unsubscribed event type is quarantined with its name, never guessed");
    }
}
