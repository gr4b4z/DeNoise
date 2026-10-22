using System.Text.Json.Nodes;
using DeNoise.Application.Mapping;
using DeNoise.Application.Policies;
using DeNoise.Application.Routing;
using DeNoise.Domain.Common;
using DeNoise.Domain.Policies;

namespace DeNoise.Application.Topology;

public static class TopologyActions
{
    /// <summary>The dependent's opening notification is muted (attributed to the parent); it stays visible, owned and actionable.</summary>
    public const string MuteChild = "mute_child";
    /// <summary>The dependent joins a group whose first member is the parent; quiet member unless critical and not muted.</summary>
    public const string GroupUnderParent = "group_under_parent";
    /// <summary>Observation mode (spec §16.5): the relation is recorded, nothing is muted.</summary>
    public const string AnnotateOnly = "annotate_only";
    public static readonly IReadOnlyList<string> All = [MuteChild, GroupUnderParent, AnnotateOnly];
}

/// <summary>One side of a dependency rule: a predicate over the episode (and its latest event) and the key fields whose values must be equal on both sides.</summary>
public sealed record TopologySide(Predicate Match, IReadOnlyList<string> Key, IReadOnlyList<string>? Static)
{
    public bool IsStatic => Static is not null;
}

/// <summary>
/// One topology rule (spec §16.5): parent and child sides, the dependency window measured from the parent's opening, the look-back
/// for children that fired first, the action and which severities a <c>mute_child</c> rule may mute (critical only when listed).
/// </summary>
public sealed record TopologyRule(Guid RuleId, string? Name, TopologySide Parent, TopologySide Child, TimeSpan Window, TimeSpan LookBack, string Action, IReadOnlyList<Severity> MuteSeverities)
{
    public string DisplayName => Name ?? RuleId.ToString();
    public bool Mutes(Severity severity) => Action == TopologyActions.MuteChild && MuteSeverities.Contains(severity);
}

/// <summary>
/// The topology policy document: an ordered list of rules; the first rule whose child side matches and whose parent is open wins.
/// Parents come from field equality or a static list only — no discovered topology (spec §16.5, §5.2).
/// </summary>
public sealed class TopologyPolicyDocument
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(15);
    /// <summary>Default for <c>mute_severities</c>: everything but <c>critical</c> and <c>unknown</c> — the most severe class and the unclassified one never disappear behind a dependency by default.</summary>
    public static readonly IReadOnlyList<Severity> DefaultMuteSeverities = [Severity.High, Severity.Medium, Severity.Low, Severity.Informational];

    public int Version { get; init; }
    public required IReadOnlyList<TopologyRule> Rules { get; init; }
    public required JsonObject Source { get; init; }

    public static TopologyPolicyDocument Parse(JsonNode root, int version)
    {
        var errors = new List<MappingValidationError>();
        if (root is not JsonObject top) throw new MappingValidationException([new MappingValidationError("$", "topology policy must be an object with a rules list")]);
        var doc = top["topology"] as JsonObject ?? top;
        var basePath = ReferenceEquals(doc, top) ? "$" : "$.topology";
        if (doc["rules"] is not JsonArray list) throw new MappingValidationException([new MappingValidationError($"{basePath}.rules", "required list of rules")]);
        var rules = new List<TopologyRule>();
        for (var i = 0; i < list.Count; i++)
        {
            var path = $"{basePath}.rules[{i}]";
            if (list[i] is not JsonObject rule)
            {
                errors.Add(new MappingValidationError(path, "must be an object"));
                continue;
            }
            var ruleId = rule["id"] is JsonValue idv && Guid.TryParse(idv.ToString(), out var parsed) ? parsed : DeterministicId(i, rule);
            var parent = ParseSide(rule["parent"], $"{path}.parent", isParent: true, errors);
            var child = ParseSide(rule["child"], $"{path}.child", isParent: false, errors);
            if (parent is not null && child is not null)
            {
                if (child.Key.Count == 0 && !parent.IsStatic) errors.Add(new MappingValidationError($"{path}.child.key", "required unless the parent is static: the child's key values must equal the parent's"));
                else if (child.Key.Count > 0 && child.Key.Count != parent.Key.Count) errors.Add(new MappingValidationError($"{path}.child.key", $"must list as many fields as parent.key ({parent.Key.Count}); values are compared position by position"));
            }
            var window = EscalationPolicyDocument.ParseDuration(rule["window"], $"{path}.window", errors) ?? DefaultWindow;
            if (window <= TimeSpan.Zero || window > TimeSpan.FromHours(24)) errors.Add(new MappingValidationError($"{path}.window", "must be between 1 second and 24 hours"));
            var lookBack = EscalationPolicyDocument.ParseDuration(rule["look_back"], $"{path}.look_back", errors) ?? TimeSpan.Zero;
            if (lookBack < TimeSpan.Zero || lookBack > TimeSpan.FromHours(24)) errors.Add(new MappingValidationError($"{path}.look_back", "must be between 0 and 24 hours"));
            var action = rule["action"]?.ToString()?.Trim().ToLowerInvariant() ?? TopologyActions.AnnotateOnly;
            if (!TopologyActions.All.Contains(action)) errors.Add(new MappingValidationError($"{path}.action", $"must be one of {string.Join(", ", TopologyActions.All)}"));
            var mute = ParseSeverities(rule["mute_severities"], $"{path}.mute_severities", errors) ?? DefaultMuteSeverities;
            if (rule.ContainsKey("release")) errors.Add(new MappingValidationError($"{path}.release", "not configurable: dependents are released when the parent closes or the window ends, whichever comes first (spec §16.5)"));
            if (parent is not null && child is not null) rules.Add(new TopologyRule(ruleId, rule["name"]?.ToString(), parent, child, window, lookBack, action, mute));
        }
        if (errors.Count == 0) DetectCycles(rules, basePath, errors);
        if (errors.Count > 0) throw new MappingValidationException(errors);
        return new TopologyPolicyDocument { Version = version, Rules = rules, Source = (JsonObject)doc.DeepClone() };
    }

    public static TopologyPolicyDocument ParseYaml(string yaml, int version)
        => Parse(YamlJson.Parse(yaml) ?? throw new MappingValidationException([new MappingValidationError("$", "document is empty")]), version);

    private static TopologySide? ParseSide(JsonNode? node, string path, bool isParent, List<MappingValidationError> errors)
    {
        if (node is not JsonObject side)
        {
            errors.Add(new MappingValidationError(path, "required object"));
            return null;
        }
        var key = new List<string>();
        if (side["key"] is JsonArray keys)
        {
            foreach (var k in keys)
            {
                var reference = k?.ToString() ?? string.Empty;
                if (!RoutingRefs.IsValid(reference)) errors.Add(new MappingValidationError($"{path}.key", $"'{reference}' is not a field reference"));
                else key.Add(reference);
            }
        }
        else if (side["key"] is not null)
        {
            errors.Add(new MappingValidationError($"{path}.key", "must be a list of field references"));
        }

        List<string>? statics = null;
        if (side["static"] is JsonNode st)
        {
            if (!isParent) errors.Add(new MappingValidationError($"{path}.static", "only a parent can be static"));
            else if (st is not JsonArray arr || arr.Count == 0) errors.Add(new MappingValidationError($"{path}.static", "must be a non-empty list of key values"));
            else statics = arr.Select(v => v?.ToString() ?? string.Empty).Where(v => v.Length > 0).ToList();
        }

        Predicate? match = null;
        if (side["match"] is not null && statics is not null) errors.Add(new MappingValidationError(path, "use either match or static, not both"));
        else if (side["match"] is not null) match = PredicateParser.Parse(side["match"], $"{path}.match", errors, RoutingRefs.IsValid);
        else if (statics is not null)
        {
            if (key.Count != 1) errors.Add(new MappingValidationError($"{path}.key", "a static parent names exactly one key field whose value is in the static list"));
            else match = new Predicate.In(key[0], statics.Select(v => (JsonNode?)JsonValue.Create(v)).ToList());
        }
        else errors.Add(new MappingValidationError($"{path}.match", isParent ? "required (or a static list)" : "required"));

        if (isParent && key.Count == 0) errors.Add(new MappingValidationError($"{path}.key", "required non-empty list of field references (e.g. [resource_id])"));
        return match is null ? null : new TopologySide(match, key, statics);
    }

    private static IReadOnlyList<Severity>? ParseSeverities(JsonNode? node, string path, List<MappingValidationError> errors)
    {
        if (node is null) return null;
        if (node is not JsonArray arr)
        {
            errors.Add(new MappingValidationError(path, "must be a list of severities"));
            return null;
        }
        var list = new List<Severity>();
        foreach (var item in arr)
        {
            var text = item?.ToString();
            if (text is null || !Enum.TryParse<Severity>(text, true, out var parsed) || !Enum.IsDefined(parsed))
            {
                errors.Add(new MappingValidationError(path, $"unknown severity '{text}'"));
                continue;
            }
            list.Add(parsed);
        }
        return list;
    }

    /// <summary>
    /// A policy whose rules form a cycle is refused (spec §16.5). Rule <c>i</c> leads to rule <c>j</c> when every episode <c>i</c> would
    /// make a child is, by its predicate, also a possible parent of <c>j</c> (structural implication of the predicates: identical
    /// predicates, an <c>eq</c>/<c>in</c> inside a wider <c>in</c>, an <c>all</c> that contains the parent's atoms, or a parent
    /// predicate that matches everything). A self-loop is the simplest cycle.
    /// </summary>
    private static void DetectCycles(List<TopologyRule> rules, string basePath, List<MappingValidationError> errors)
    {
        var n = rules.Count;
        var edges = new List<int>[n];
        for (var i = 0; i < n; i++)
        {
            edges[i] = [];
            for (var j = 0; j < n; j++)
            {
                if (PredicateImplication.Implies(rules[i].Child.Match, rules[j].Parent.Match)) edges[i].Add(j);
            }
        }
        var state = new int[n]; // 0 unvisited, 1 on stack, 2 done
        var stack = new List<int>();
        for (var start = 0; start < n; start++)
        {
            if (state[start] != 0) continue;
            if (Visit(start, edges, state, stack, out var cycle))
            {
                var names = string.Join(" → ", cycle.Select(k => rules[k].DisplayName));
                errors.Add(new MappingValidationError($"{basePath}.rules[{cycle[0]}]", $"rules form a dependency cycle ({names} → {rules[cycle[0]].DisplayName}): the children of one rule can be the parents of the next and back again; a cycle is refused (spec §16.5)"));
                return;
            }
        }
    }

    private static bool Visit(int node, List<int>[] edges, int[] state, List<int> stack, out List<int> cycle)
    {
        state[node] = 1;
        stack.Add(node);
        foreach (var next in edges[node])
        {
            if (state[next] == 1)
            {
                cycle = stack.Skip(stack.IndexOf(next)).ToList();
                return true;
            }
            if (state[next] == 0 && Visit(next, edges, state, stack, out cycle)) return true;
        }
        stack.RemoveAt(stack.Count - 1);
        state[node] = 2;
        cycle = [];
        return false;
    }

    private static Guid DeterministicId(int index, JsonObject rule)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("topology:" + index + ":" + rule.ToJsonString()));
        return new Guid(bytes.AsSpan(0, 16));
    }
}

/// <summary>Structural implication between predicates — enough to see the cycles a person would write, never a satisfiability solver.</summary>
public static class PredicateImplication
{
    public static bool Implies(Predicate from, Predicate to)
    {
        if (Same(from, to)) return true;
        switch (to)
        {
            case Predicate.All all when all.Items.Count == 0: return true;
            case Predicate.All all: return all.Items.All(item => Implies(from, item));
            case Predicate.Any any when any.Items.Any(item => Implies(from, item)): return true;
        }
        switch (from)
        {
            case Predicate.All all: return all.Items.Any(item => Implies(item, to));
            case Predicate.Any any: return any.Items.Count > 0 && any.Items.All(item => Implies(item, to));
            case Predicate.Eq eq:
                return to switch
                {
                    Predicate.In @in => @in.Ref == eq.Ref && @in.Values.Any(v => JsonNode.DeepEquals(v, eq.Value)),
                    Predicate.Exists ex => ex.Ref == eq.Ref && eq.Value is not null,
                    _ => false,
                };
            case Predicate.In @in:
                return to switch
                {
                    Predicate.In wider => wider.Ref == @in.Ref && @in.Values.All(v => wider.Values.Any(w => JsonNode.DeepEquals(v, w))),
                    Predicate.Exists ex => ex.Ref == @in.Ref,
                    _ => false,
                };
            default: return false;
        }
    }

    public static bool Same(Predicate a, Predicate b) => (a, b) switch
    {
        (Predicate.Eq x, Predicate.Eq y) => x.Ref == y.Ref && JsonNode.DeepEquals(x.Value, y.Value),
        (Predicate.Neq x, Predicate.Neq y) => x.Ref == y.Ref && JsonNode.DeepEquals(x.Value, y.Value),
        (Predicate.In x, Predicate.In y) => x.Ref == y.Ref && x.Values.Count == y.Values.Count && x.Values.All(v => y.Values.Any(w => JsonNode.DeepEquals(v, w))),
        (Predicate.Nin x, Predicate.Nin y) => x.Ref == y.Ref && x.Values.Count == y.Values.Count && x.Values.All(v => y.Values.Any(w => JsonNode.DeepEquals(v, w))),
        (Predicate.Regex x, Predicate.Regex y) => x.Ref == y.Ref && x.Pattern.ToString() == y.Pattern.ToString(),
        (Predicate.Exists x, Predicate.Exists y) => x.Ref == y.Ref,
        (Predicate.Gte x, Predicate.Gte y) => x.Ref == y.Ref && JsonNode.DeepEquals(x.Value, y.Value),
        (Predicate.Lte x, Predicate.Lte y) => x.Ref == y.Ref && JsonNode.DeepEquals(x.Value, y.Value),
        (Predicate.All x, Predicate.All y) => x.Items.Count == y.Items.Count && x.Items.Zip(y.Items).All(p => Same(p.First, p.Second)),
        (Predicate.Any x, Predicate.Any y) => x.Items.Count == y.Items.Count && x.Items.Zip(y.Items).All(p => Same(p.First, p.Second)),
        (Predicate.Not x, Predicate.Not y) => Same(x.Item, y.Item),
        _ => false,
    };
}

public sealed class TopologyPolicyValidator : IPolicyValidator
{
    public string Kind => PolicyKinds.Topology;
    public void Validate(JsonNode body, int version) => TopologyPolicyDocument.Parse(body, version);
}
