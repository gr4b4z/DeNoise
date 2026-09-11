using AlertHub.Application.Mapping;
using AlertHub.Application.Notifications;
using AlertHub.Application.Routing;
using AlertHub.Domain.Alerts;
using AlertHub.Domain.Common;
using AlertHub.Domain.Episodes;
using AlertHub.Domain.Integrations;
using AlertHub.Domain.Teams;
using Microsoft.Extensions.Time.Testing;

namespace AlertHub.Application.Tests.Routing;

[Trait("Category", "Unit")]
public sealed class RoutingEngineTests
{
    private static readonly Guid TeamMpt = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TeamPlatform = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid TeamTriage = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000009");
    private static readonly Guid DestExtra = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid Escalation = Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");

    private static readonly string Yaml = $$"""
        routing:
          rules:
            - name: mpt-prod
              priority: 10
              match: { all: [ { eq: [environment, production] }, { in: [service, [marketplace, mpt-api]] } ] }
              team: {{TeamMpt}}
              escalation_policy: {{Escalation}}
            - name: platform-critical
              priority: 20
              match: { gte: [severity, critical] }
              team: {{TeamPlatform}}
              destinations: [{{DestExtra}}]
              stop: false
            - name: platform-azure
              priority: 30
              match: { eq: [integration.type, azure_monitor] }
              team: {{TeamPlatform}}
            - name: labelled
              priority: 40
              match: { exists: labels.team }
              team: {{TeamMpt}}
        """;

    private static readonly IReadOnlySet<Guid> KnownTeams = new HashSet<Guid> { TeamMpt, TeamPlatform, TeamTriage };
    private static readonly Team Triage = new() { TeamId = TeamTriage, Name = "triage", IsTriage = true };

    private static (NormalisedEvent Evt, Episode Episode, Integration Integration) Make(string environment = "production", string service = "marketplace", Severity severity = Severity.High, string type = "generic_webhook", IReadOnlyDictionary<string, string>? labels = null)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
        var integration = new Integration { IntegrationId = Guid.NewGuid(), Name = "int", Type = type, AccessScope = "s", IngestKeyId = "abcdefghjkmn", IngestTokenHash = "x" };
        var evt = new NormalisedEvent
        {
            EventId = Guid.NewGuid(),
            IntegrationId = integration.IntegrationId,
            EventType = EventTypes.Firing,
            OccurredAt = time.GetUtcNow(),
            ReceivedAt = time.GetUtcNow(),
            RawReceivedAt = time.GetUtcNow(),
            Severity = severity,
            Environment = environment,
            Service = service,
            Labels = labels,
            DeliveryKey = "k",
            IdentityConfidence = "exact",
            Fingerprint = new string('b', 64),
        };
        var episode = Episode.Open(evt, "s", time.GetUtcNow(), time);
        return (evt, episode, integration);
    }

    [Fact]
    public void First_matching_rule_by_priority_wins_and_carries_its_escalation_policy()
    {
        var policy = RoutingPolicyDocument.ParseYaml(Yaml, 1);
        var (evt, episode, integration) = Make();
        var decision = RoutingEngine.Route(policy, evt, episode, integration, Triage, KnownTeams);
        decision.TeamId.Should().Be(TeamMpt);
        decision.RuleName.Should().Be("mpt-prod");
        decision.EscalationPolicyId.Should().Be(Escalation);
        decision.CorrectionRequired.Should().BeFalse();
        decision.Why.Should().Contain("mpt-prod");
    }

    [Fact]
    public void Stop_false_adds_destinations_and_continues_to_the_next_rule()
    {
        var policy = RoutingPolicyDocument.ParseYaml(Yaml, 1);
        var (evt, episode, integration) = Make(service: "other", severity: Severity.Critical, type: "azure_monitor");
        var decision = RoutingEngine.Route(policy, evt, episode, integration, Triage, KnownTeams);
        decision.TeamId.Should().Be(TeamPlatform);
        decision.RuleName.Should().Be("platform-critical");
        decision.ExtraDestinations.Should().Equal(DestExtra);
        decision.Why.Should().Contain("platform-azure");
    }

    [Fact]
    public void Unknown_severity_ranks_as_high_and_labels_are_addressable()
    {
        var policy = RoutingPolicyDocument.ParseYaml(Yaml, 1);
        var (evt, episode, integration) = Make(service: "other", severity: Severity.Unknown, labels: new Dictionary<string, string> { ["team"] = "x" });
        var decision = RoutingEngine.Route(policy, evt, episode, integration, Triage, KnownTeams);
        decision.RuleName.Should().Be("labelled", "unknown is not ≥ critical, so the label rule applies");
    }

    [Fact]
    public void No_match_falls_back_to_triage_with_correction_flag_and_no_match_without_triage_uses_owner_or_unassigned()
    {
        var policy = RoutingPolicyDocument.ParseYaml(Yaml, 1);
        var (evt, episode, integration) = Make(environment: "staging", service: "other", severity: Severity.Low);

        var toTriage = RoutingEngine.Route(policy, evt, episode, integration, Triage, KnownTeams);
        toTriage.TeamId.Should().Be(TeamTriage);
        toTriage.CorrectionRequired.Should().BeTrue();
        toTriage.RuleId.Should().BeNull();

        integration.OwnerTeamId = TeamPlatform;
        var toOwner = RoutingEngine.Route(policy, evt, episode, integration, null, KnownTeams);
        toOwner.TeamId.Should().Be(TeamPlatform);
        toOwner.CorrectionRequired.Should().BeTrue();

        integration.OwnerTeamId = null;
        var unassigned = RoutingEngine.Route(null, evt, episode, integration, null, KnownTeams);
        unassigned.TeamId.Should().BeNull();
        unassigned.CorrectionRequired.Should().BeTrue();
        unassigned.Why.Should().Contain("no active routing policy");
    }

    [Fact]
    public void Rules_naming_unknown_teams_are_skipped_not_applied()
    {
        var policy = RoutingPolicyDocument.ParseYaml(Yaml, 1);
        var (evt, episode, integration) = Make();
        var decision = RoutingEngine.Route(policy, evt, episode, integration, Triage, new HashSet<Guid> { TeamTriage });
        decision.TeamId.Should().Be(TeamTriage);
        decision.CorrectionRequired.Should().BeTrue();
        decision.Why.Should().Contain("unknown team");
    }

    [Fact]
    public void Routing_policy_validation_rejects_raw_jsonpath_refs_and_bad_ids()
    {
        var act = () => RoutingPolicyDocument.ParseYaml("""
            routing:
              rules:
                - match: { eq: ["$.data.x", 1] }
                  team: not-a-uuid
                - team: aaaaaaaa-0000-0000-0000-000000000001
            """, 1);
        var errors = act.Should().Throw<MappingValidationException>().Which.Errors.Select(e => e.ToString()).ToList();
        errors.Should().Contain(e => e.Contains("$.routing.rules[0].match.eq[0]", StringComparison.Ordinal));
        errors.Should().Contain(e => e.Contains("$.routing.rules[0].team", StringComparison.Ordinal));
        errors.Should().Contain(e => e.Contains("$.routing.rules[1].match", StringComparison.Ordinal));
    }

    [Fact]
    public void Escalation_policy_parses_durations_steps_and_targets()
    {
        var policy = EscalationPolicyDocument.ParseYaml($$"""
            escalation:
              ack_deadline: 15m
              follow_up_window: PT2H
              steps:
                - { after: 0m, targets: [team_destinations] }
                - { after: 15m, targets: [ "destination:{{DestExtra}}" ] }
                - { after: 30m, targets: [ "external:oncall_bridge" ] }
              repeat_last_step_every: 1h
              max_repeats: 2
              business_hours_only: true
            """, 1);
        policy.AckDeadline.Should().Be(TimeSpan.FromMinutes(15));
        policy.FollowUpWindow.Should().Be(TimeSpan.FromHours(2));
        policy.Steps.Should().HaveCount(3);
        policy.Steps[1].Targets[0].Should().Be(new EscalationTarget(EscalationTarget.Destination, DestExtra.ToString()));
        policy.Steps[2].Targets[0].Kind.Should().Be(EscalationTarget.External);
        policy.RepeatLastStepEvery.Should().Be(TimeSpan.FromHours(1));
        policy.MaxRepeats.Should().Be(2);
        policy.BusinessHoursOnly.Should().BeTrue();

        var bad = () => EscalationPolicyDocument.ParseYaml("escalation:\n  ack_deadline: soon\n  steps: [ { after: 0m, targets: [ 'pager:x' ] } ]\n", 1);
        var errors = bad.Should().Throw<MappingValidationException>().Which.Errors;
        errors.Should().Contain(e => e.Message.Contains("unknown target", StringComparison.Ordinal));
    }

    [Fact]
    public void Delivery_backoff_follows_adr7_and_honours_retry_after()
    {
        DeliveryBackoff.For(1).Should().Be(TimeSpan.FromSeconds(30));
        DeliveryBackoff.For(2).Should().Be(TimeSpan.FromMinutes(1));
        DeliveryBackoff.For(3).Should().Be(TimeSpan.FromMinutes(5));
        DeliveryBackoff.For(4).Should().Be(TimeSpan.FromMinutes(15));
        DeliveryBackoff.For(5).Should().Be(TimeSpan.FromHours(1));
        DeliveryBackoff.For(9).Should().Be(TimeSpan.FromHours(1));
        DeliveryBackoff.For(1, TimeSpan.FromSeconds(90)).Should().Be(TimeSpan.FromSeconds(90));
        DeliveryBackoff.For(1, TimeSpan.FromDays(2)).Should().Be(TimeSpan.FromHours(6), "an absurd Retry-After is capped");
    }
}
