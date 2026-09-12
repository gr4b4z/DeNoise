using System.Text.Json.Nodes;
using AlertHub.Application.Mapping;
using AlertHub.Application.Policies;
using AlertHub.Application.Routing;
using AlertHub.Domain.Policies;

namespace AlertHub.Application.Grouping;

public static class GroupNotify
{
    public const string FirstOnly = "first_only";
    public const string FirstAndNewCritical = "first_and_new_critical";
    public static readonly IReadOnlyList<string> All = [FirstOnly, FirstAndNewCritical];
}

/// <summary>One grouping rule (04 §7.4): <c>match</c>, <c>key</c> field references, fixed <c>window</c> from the first member, <c>notify</c> policy.</summary>
public sealed record GroupingRule(Guid RuleId, string? Name, Predicate Match, IReadOnlyList<string> Key, TimeSpan Window, string Notify);

/// <summary>The grouping policy document: an ordered list of rules; the first matching rule groups the episode.</summary>
public sealed class GroupingPolicyDocument
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(10);

    public int Version { get; init; }
    public required IReadOnlyList<GroupingRule> Rules { get; init; }
    public required JsonObject Source { get; init; }

    public static GroupingPolicyDocument Parse(JsonNode root, int version)
    {
        var errors = new List<MappingValidationError>();
        if (root is not JsonObject top) throw new MappingValidationException([new MappingValidationError("$", "grouping policy must be an object with a rules list")]);
        var doc = top["grouping"] as JsonObject ?? top;
        var basePath = ReferenceEquals(doc, top) ? "$" : "$.grouping";
        if (doc["rules"] is not JsonArray list) throw new MappingValidationException([new MappingValidationError($"{basePath}.rules", "required list of rules")]);
        var rules = new List<GroupingRule>();
        for (var i = 0; i < list.Count; i++)
        {
            var path = $"{basePath}.rules[{i}]";
            if (list[i] is not JsonObject rule)
            {
                errors.Add(new MappingValidationError(path, "must be an object"));
                continue;
            }
            var ruleId = rule["id"] is JsonValue idv && Guid.TryParse(idv.ToString(), out var parsed) ? parsed : DeterministicId(i, rule);
            Predicate? match = null;
            if (rule["match"] is null) errors.Add(new MappingValidationError($"{path}.match", "required"));
            else match = PredicateParser.Parse(rule["match"], $"{path}.match", errors, RoutingRefs.IsValid);
            var key = new List<string>();
            if (rule["key"] is JsonArray keys && keys.Count > 0)
            {
                foreach (var k in keys)
                {
                    var reference = k?.ToString() ?? string.Empty;
                    if (!RoutingRefs.IsValid(reference)) errors.Add(new MappingValidationError($"{path}.key", $"'{reference}' is not a field reference"));
                    else key.Add(reference);
                }
            }
            else
            {
                errors.Add(new MappingValidationError($"{path}.key", "required non-empty list of field references (e.g. [service, environment])"));
            }
            var window = EscalationPolicyDocument.ParseDuration(rule["window"], $"{path}.window", errors) ?? DefaultWindow;
            if (window <= TimeSpan.Zero || window > TimeSpan.FromHours(24)) errors.Add(new MappingValidationError($"{path}.window", "must be between 1 second and 24 hours"));
            var notify = rule["notify"]?.ToString()?.Trim().ToLowerInvariant() ?? GroupNotify.FirstAndNewCritical;
            if (!GroupNotify.All.Contains(notify)) errors.Add(new MappingValidationError($"{path}.notify", $"must be one of {string.Join(", ", GroupNotify.All)}"));
            if (match is not null) rules.Add(new GroupingRule(ruleId, rule["name"]?.ToString(), match, key, window, notify));
        }
        if (errors.Count > 0) throw new MappingValidationException(errors);
        return new GroupingPolicyDocument { Version = version, Rules = rules, Source = (JsonObject)doc.DeepClone() };
    }

    public static GroupingPolicyDocument ParseYaml(string yaml, int version)
        => Parse(YamlJson.Parse(yaml) ?? throw new MappingValidationException([new MappingValidationError("$", "document is empty")]), version);

    private static Guid DeterministicId(int index, JsonObject rule)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("grouping:" + index + ":" + rule.ToJsonString()));
        return new Guid(bytes.AsSpan(0, 16));
    }
}

public sealed class GroupingPolicyValidator : IPolicyValidator
{
    public string Kind => PolicyKinds.Grouping;
    public void Validate(JsonNode body, int version) => GroupingPolicyDocument.Parse(body, version);
}
