using System.Text.Json.Nodes;
using AlertHub.Application.Mapping;
using AlertHub.Domain.Alerts;
using AlertHub.Domain.Common;
using AlertHub.Domain.Episodes;
using AlertHub.Domain.Integrations;
using AlertHub.Domain.Teams;

namespace AlertHub.Application.Routing;

/// <summary>Outcome of routing one episode (04 §7.1, spec §16.1). <see cref="Why"/> is shown in the UI as "which rule applied and why".</summary>
public sealed record RoutingDecision(Guid? TeamId, Guid? RuleId, string? RuleName, IReadOnlyList<Guid> ExtraDestinations, Guid? EscalationPolicyId, bool CorrectionRequired, string Why, Guid? LifecyclePolicyId = null)
{
    public bool IsRoutingFailure => CorrectionRequired;
}

/// <summary>Routing selects the accountable team first, destinations second (spec §16.1). Pure function; no I/O.</summary>
public static class RoutingEngine
{
    public static RoutingDecision Route(RoutingPolicyDocument? policy, NormalisedEvent evt, Episode episode, Integration integration, Team? triageTeam, IReadOnlySet<Guid> knownTeams)
    {
        Guid? team = null;
        Guid? ruleId = null;
        string? ruleName = null;
        Guid? escalation = null;
        Guid? lifecycle = null;
        var destinations = new List<Guid>();
        var trail = new List<string>();

        if (policy is not null)
        {
            JsonNode? Resolve(string reference) => ResolveRef(reference, evt, episode, integration);
            foreach (var rule in policy.Rules)
            {
                if (!PredicateEvaluator.Evaluate(rule.Match, Resolve)) continue;
                if (!knownTeams.Contains(rule.TeamId))
                {
                    trail.Add($"rule {rule.Name ?? rule.RuleId.ToString()} matched but names an unknown team {rule.TeamId}; skipped");
                    continue;
                }
                destinations.AddRange(rule.Destinations);
                if (team is null)
                {
                    team = rule.TeamId;
                    ruleId = rule.RuleId;
                    ruleName = rule.Name;
                    escalation = rule.EscalationPolicyId;
                    lifecycle = rule.LifecyclePolicyId;
                    trail.Add($"rule '{rule.Name ?? rule.RuleId.ToString()}' (priority {rule.Priority}) matched → team {rule.TeamId}");
                }
                else
                {
                    trail.Add($"rule '{rule.Name ?? rule.RuleId.ToString()}' (stop: false) added {rule.Destinations.Count} destination(s)");
                }
                if (rule.Stop) break;
            }
        }
        else
        {
            trail.Add("no active routing policy");
        }

        if (team is not null)
        {
            return new RoutingDecision(team, ruleId, ruleName, destinations.Distinct().ToList(), escalation, false, string.Join("; ", trail), lifecycle);
        }

        // No match: fallback triage team, visibly flagged (spec §7.2). Never discarded.
        if (triageTeam is not null)
        {
            trail.Add($"no rule matched → fallback triage team '{triageTeam.Name}'; routing correction required");
            return new RoutingDecision(triageTeam.TeamId, null, null, destinations, triageTeam.DefaultEscalationPolicyId, true, string.Join("; ", trail));
        }
        if (integration.OwnerTeamId is { } owner && knownTeams.Contains(owner))
        {
            trail.Add("no rule matched and no triage team is configured → integration owner team; routing correction required");
            return new RoutingDecision(owner, null, null, destinations, null, true, string.Join("; ", trail));
        }
        trail.Add("no rule matched, no triage team, no owner team → unassigned; routing correction required");
        return new RoutingDecision(null, null, null, destinations, null, true, string.Join("; ", trail));
    }

    /// <summary>Predicate references over the normalised event + episode + integration (07 §5).</summary>
    public static JsonNode? ResolveRef(string reference, NormalisedEvent evt, Episode episode, Integration integration)
    {
        switch (reference)
        {
            case CanonicalFields.Severity: return JsonValue.Create(episode.Severity.ToWire());
            case CanonicalFields.SourceSeverity: return Value(evt.SourceSeverity);
            case CanonicalFields.EventType: return JsonValue.Create(evt.EventType);
            case CanonicalFields.SourceAlertId: return Value(evt.SourceAlertId);
            case CanonicalFields.SourceEventId: return Value(evt.SourceEventId);
            case CanonicalFields.SourceVersion: return Value(evt.SourceVersion);
            case CanonicalFields.OccurredAt: return evt.OccurredAt is { } o ? JsonValue.Create(o.ToString("O")) : null;
            case CanonicalFields.ResourceId: return Value(evt.ResourceId);
            case CanonicalFields.ResourceName: return Value(evt.ResourceName ?? episode.ResourceName);
            case CanonicalFields.RuleId: return Value(evt.RuleId);
            case CanonicalFields.RuleName: return Value(evt.RuleName ?? episode.RuleName);
            case CanonicalFields.Environment: return Value(evt.Environment ?? episode.Environment);
            case CanonicalFields.Service: return Value(evt.Service ?? episode.Service);
            case CanonicalFields.Summary: return Value(evt.Summary ?? episode.Summary);
            case CanonicalFields.SourceUrl: return Value(evt.SourceUrl);
            case CanonicalFields.RunbookUrl: return Value(evt.RunbookUrl ?? episode.RunbookUrl);
            case "integration.type": return JsonValue.Create(integration.Type);
            case "integration.id": return JsonValue.Create(integration.IntegrationId.ToString());
            case "integration.name": return JsonValue.Create(integration.Name);
            case "access_scope": return JsonValue.Create(episode.AccessScope);
            case "condition_state": return JsonValue.Create(episode.ConditionState);
            case "handling_state": return JsonValue.Create(episode.HandlingState);
            case "owning_team_id": return episode.OwningTeamId is { } t ? JsonValue.Create(t.ToString()) : null;
            case "is_actionable": return JsonValue.Create(episode.IsActionable);
        }
        if (reference.StartsWith("labels.", StringComparison.Ordinal))
        {
            return evt.Labels is not null && evt.Labels.TryGetValue(reference["labels.".Length..], out var l) ? JsonValue.Create(l) : null;
        }
        if (reference.StartsWith("dimensions.", StringComparison.Ordinal))
        {
            return evt.Dimensions is not null && evt.Dimensions.TryGetValue(reference["dimensions.".Length..], out var d) ? JsonValue.Create(d) : null;
        }
        return null;
    }

    private static JsonNode? Value(string? s) => s is null ? null : JsonValue.Create(s);
}
