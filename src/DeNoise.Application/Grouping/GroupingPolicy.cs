using System.Globalization;
using System.Text.Json.Nodes;
using DeNoise.Application.Mapping;
using DeNoise.Application.Policies;
using DeNoise.Application.Routing;
using DeNoise.Domain.Policies;

namespace DeNoise.Application.Grouping;

public static class GroupNotify
{
    public const string FirstOnly = "first_only";
    public const string FirstAndNewCritical = "first_and_new_critical";
    /// <summary>First member notifies; afterwards a periodic roll-up of what joined (spec §16.3 "Digest").</summary>
    public const string Digest = "digest";
    public static readonly IReadOnlyList<string> All = [FirstOnly, FirstAndNewCritical, Digest];
}

/// <summary>
/// The group window (spec §16.3 "bounded, not fixed"): <see cref="Initial"/> from the first member, each member pushes the end out by
/// <see cref="ExtendBy"/> but never past first member + <see cref="Max"/>, and a member arriving inside <see cref="LateJoin"/> after the
/// end still joins. A scalar <c>window: 10m</c> is <see cref="Initial"/> alone — today's fixed window.
/// </summary>
public sealed record GroupWindow(TimeSpan Initial, TimeSpan? ExtendBy, TimeSpan Max, TimeSpan LateJoin)
{
    public bool Extends => ExtendBy is { } e && e > TimeSpan.Zero;
}

/// <summary>Periodic roll-up of a group (spec §16.3 "Digest"): one notification per <see cref="Every"/> with at most <see cref="MaxItems"/> members.</summary>
public sealed record GroupDigest(TimeSpan Every, int MaxItems);

/// <summary>One grouping rule (04 §7.4, spec §16.3): <c>match</c>, <c>key</c> field references, the window, retro adoption look-back, minimum member count, notify policy and optional digest.</summary>
public sealed record GroupingRule(Guid RuleId, string? Name, Predicate Match, IReadOnlyList<string> Key, GroupWindow Window, TimeSpan LookBack, int MinMembers, string Notify, GroupDigest? Digest)
{
    public string DisplayName => Name ?? RuleId.ToString();
}

/// <summary>The grouping policy document: an ordered list of rules; the first matching rule groups the episode.</summary>
public sealed class GroupingPolicyDocument
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxWindow = TimeSpan.FromHours(24);
    public const int DefaultDigestMaxItems = 20;

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
            var window = ParseWindow(rule["window"], $"{path}.window", errors);
            var lookBack = TimeSpan.Zero;
            if (rule["retro"] is JsonObject retro)
            {
                lookBack = EscalationPolicyDocument.ParseDuration(retro["look_back"], $"{path}.retro.look_back", errors) ?? TimeSpan.Zero;
                if (lookBack < TimeSpan.Zero || lookBack > MaxWindow) errors.Add(new MappingValidationError($"{path}.retro.look_back", "must be between 0 and 24 hours"));
            }
            else if (rule["retro"] is not null) errors.Add(new MappingValidationError($"{path}.retro", "must be an object with look_back"));
            var minMembers = 1;
            if (rule["min_members"] is JsonValue mm)
            {
                if (!(mm.TryGetValue<int>(out minMembers) || int.TryParse(mm.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out minMembers)) || minMembers < 1 || minMembers > 100)
                {
                    errors.Add(new MappingValidationError($"{path}.min_members", "must be an integer between 1 and 100"));
                    minMembers = 1;
                }
            }
            var notify = rule["notify"]?.ToString()?.Trim().ToLowerInvariant() ?? GroupNotify.FirstAndNewCritical;
            if (!GroupNotify.All.Contains(notify)) errors.Add(new MappingValidationError($"{path}.notify", $"must be one of {string.Join(", ", GroupNotify.All)}"));
            GroupDigest? digest = null;
            if (rule["digest"] is JsonObject dg)
            {
                var every = EscalationPolicyDocument.ParseDuration(dg["every"], $"{path}.digest.every", errors);
                if (every is null) errors.Add(new MappingValidationError($"{path}.digest.every", "required (e.g. 5m)"));
                else if (every <= TimeSpan.Zero || every > MaxWindow) errors.Add(new MappingValidationError($"{path}.digest.every", "must be between 1 second and 24 hours"));
                var maxItems = DefaultDigestMaxItems;
                if (dg["max_items"] is JsonValue mi && (!(mi.TryGetValue<int>(out maxItems) || int.TryParse(mi.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out maxItems)) || maxItems < 1 || maxItems > 500))
                {
                    errors.Add(new MappingValidationError($"{path}.digest.max_items", "must be an integer between 1 and 500"));
                    maxItems = DefaultDigestMaxItems;
                }
                if (notify != GroupNotify.Digest) errors.Add(new MappingValidationError($"{path}.notify", "must be 'digest' when a digest block is configured"));
                digest = new GroupDigest(every ?? TimeSpan.FromMinutes(5), maxItems);
            }
            else if (rule["digest"] is not null) errors.Add(new MappingValidationError($"{path}.digest", "must be an object with every and max_items"));
            else if (notify == GroupNotify.Digest) errors.Add(new MappingValidationError($"{path}.digest", "required when notify is 'digest' (every, max_items)"));
            if (match is not null) rules.Add(new GroupingRule(ruleId, rule["name"]?.ToString(), match, key, window, lookBack, minMembers, notify, digest));
        }
        if (errors.Count > 0) throw new MappingValidationException(errors);
        return new GroupingPolicyDocument { Version = version, Rules = rules, Source = (JsonObject)doc.DeepClone() };
    }

    /// <summary>
    /// <c>window: 10m</c> (scalar, fixed — today's behaviour) or <c>window: { initial, extend_by, max, late_join }</c>. <c>max</c> is mandatory
    /// whenever <c>extend_by</c> is present: a group extensible without bound is an alert that is never delivered (spec §16.3).
    /// </summary>
    private static GroupWindow ParseWindow(JsonNode? node, string path, List<MappingValidationError> errors)
    {
        if (node is JsonObject w)
        {
            var initial = EscalationPolicyDocument.ParseDuration(w["initial"], $"{path}.initial", errors) ?? DefaultWindow;
            if (initial <= TimeSpan.Zero || initial > MaxWindow) errors.Add(new MappingValidationError($"{path}.initial", "must be between 1 second and 24 hours"));
            var extendBy = EscalationPolicyDocument.ParseDuration(w["extend_by"], $"{path}.extend_by", errors);
            if (extendBy is { } eb && (eb <= TimeSpan.Zero || eb > MaxWindow)) errors.Add(new MappingValidationError($"{path}.extend_by", "must be between 1 second and 24 hours"));
            var max = EscalationPolicyDocument.ParseDuration(w["max"], $"{path}.max", errors);
            if (extendBy is not null && max is null) errors.Add(new MappingValidationError($"{path}.max", "required when extend_by is set: an unbounded group is a dropped alert (spec §16.3)"));
            if (max is { } m && (m < initial || m > MaxWindow)) errors.Add(new MappingValidationError($"{path}.max", "must be at least initial and at most 24 hours"));
            var lateJoin = EscalationPolicyDocument.ParseDuration(w["late_join"], $"{path}.late_join", errors) ?? TimeSpan.Zero;
            if (lateJoin < TimeSpan.Zero || lateJoin > MaxWindow) errors.Add(new MappingValidationError($"{path}.late_join", "must be between 0 and 24 hours"));
            return new GroupWindow(initial, extendBy, max ?? initial, lateJoin);
        }
        var fixedWindow = EscalationPolicyDocument.ParseDuration(node, path, errors) ?? DefaultWindow;
        if (fixedWindow <= TimeSpan.Zero || fixedWindow > MaxWindow) errors.Add(new MappingValidationError(path, "must be between 1 second and 24 hours"));
        return new GroupWindow(fixedWindow, null, fixedWindow, TimeSpan.Zero);
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
