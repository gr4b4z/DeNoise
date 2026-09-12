using System.Text.Json.Nodes;
using DeNoise.Application.Mapping;

namespace DeNoise.Application.Routing;

/// <summary>One routing rule (04 §7.1).</summary>
public sealed record RoutingRule(Guid RuleId, string? Name, int Priority, Predicate Match, Guid TeamId, IReadOnlyList<Guid> Destinations, Guid? EscalationPolicyId, bool Stop, Guid? LifecyclePolicyId = null);

/// <summary>Ordered rule set of the active routing policy. Evaluation: first match wins unless <c>stop: false</c> (adds destinations, continues).</summary>
public sealed class RoutingPolicyDocument
{
    public int Version { get; init; }
    public required IReadOnlyList<RoutingRule> Rules { get; init; }
    public required JsonObject Source { get; init; }

    public static RoutingPolicyDocument Parse(JsonNode root, int version)
    {
        var errors = new List<MappingValidationError>();
        if (root is not JsonObject top) throw new MappingValidationException([new MappingValidationError("$", "routing policy must be an object with a rules list")]);
        var doc = top["routing"] as JsonObject ?? top;
        var basePath = ReferenceEquals(doc, top) ? "$" : "$.routing";
        var rules = new List<RoutingRule>();
        if (doc["rules"] is not JsonArray list)
        {
            throw new MappingValidationException([new MappingValidationError($"{basePath}.rules", "required list of rules")]);
        }
        for (var i = 0; i < list.Count; i++)
        {
            var path = $"{basePath}.rules[{i}]";
            if (list[i] is not JsonObject rule)
            {
                errors.Add(new MappingValidationError(path, "must be an object"));
                continue;
            }
            var priority = rule["priority"] is JsonValue pv && MappingParser.TryGetInt(pv, out var p) ? p : 1000;
            var ruleId = rule["id"] is JsonValue idv && Guid.TryParse(idv.ToString(), out var parsedId) ? parsedId : DeterministicId(i, rule);
            Predicate? match = null;
            if (rule["match"] is null) errors.Add(new MappingValidationError($"{path}.match", "required"));
            else match = PredicateParser.Parse(rule["match"], $"{path}.match", errors, RoutingRefs.IsValid);
            if (rule["team"] is not JsonValue tv || !Guid.TryParse(tv.ToString(), out var teamId))
            {
                errors.Add(new MappingValidationError($"{path}.team", "must be a team id (uuid)"));
                teamId = Guid.Empty;
            }
            var destinations = new List<Guid>();
            if (rule["destinations"] is JsonArray ds)
            {
                foreach (var d in ds)
                {
                    if (d is JsonValue dv && Guid.TryParse(dv.ToString(), out var did)) destinations.Add(did);
                    else errors.Add(new MappingValidationError($"{path}.destinations", "must be destination ids (uuid)"));
                }
            }
            Guid? escalation = null;
            if (rule["escalation_policy"] is JsonValue ev)
            {
                if (Guid.TryParse(ev.ToString(), out var eid)) escalation = eid;
                else errors.Add(new MappingValidationError($"{path}.escalation_policy", "must be a policy id (uuid)"));
            }
            Guid? lifecycle = null;
            if (rule["lifecycle_policy"] is JsonValue lv)
            {
                if (Guid.TryParse(lv.ToString(), out var lid)) lifecycle = lid;
                else errors.Add(new MappingValidationError($"{path}.lifecycle_policy", "must be a policy id (uuid)"));
            }
            var stop = rule["stop"] is not JsonValue sv || !sv.TryGetValue<bool>(out var s) || s;
            if (match is not null) rules.Add(new RoutingRule(ruleId, rule["name"]?.ToString(), priority, match, teamId, destinations, escalation, stop, lifecycle));
        }
        if (errors.Count > 0) throw new MappingValidationException(errors);
        return new RoutingPolicyDocument { Version = version, Rules = rules.OrderBy(r => r.Priority).ToList(), Source = (JsonObject)doc.DeepClone() };
    }

    public static RoutingPolicyDocument ParseYaml(string yaml, int version)
        => Parse(YamlJson.Parse(yaml) ?? throw new MappingValidationException([new MappingValidationError("$", "document is empty")]), version);

    /// <summary>Rules without an explicit id get a stable id derived from their content, so routing explanations survive re-saves.</summary>
    private static Guid DeterministicId(int index, JsonObject rule)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(index + ":" + rule.ToJsonString()));
        return new Guid(bytes.AsSpan(0, 16));
    }
}

/// <summary>References allowed in routing/grouping/suppression predicates (07 §5): normalised event + episode + integration, never raw JSON.</summary>
public static class RoutingRefs
{
    public static bool IsValid(string reference) => !reference.StartsWith('$') && MappingParser.IsFieldRef(reference);
}
