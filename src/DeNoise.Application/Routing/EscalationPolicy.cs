using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml;
using DeNoise.Application.Mapping;

namespace DeNoise.Application.Routing;

/// <summary>Escalation step target (04 §7.2): <c>team_destinations</c>, <c>destination:&lt;id&gt;</c>, <c>user:&lt;id&gt;</c>, <c>external:&lt;name&gt;</c>.</summary>
public sealed record EscalationTarget(string Kind, string? Value)
{
    public const string TeamDestinations = "team_destinations";
    public const string Destination = "destination";
    public const string User = "user";
    public const string External = "external";

    public static EscalationTarget Parse(string text, string path, List<MappingValidationError> errors)
    {
        if (text == TeamDestinations) return new EscalationTarget(TeamDestinations, null);
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0)
        {
            var kind = text[..colon];
            var value = text[(colon + 1)..];
            if (kind is Destination or User or External && value.Length > 0)
            {
                if (kind is Destination or User && !Guid.TryParse(value, out _)) errors.Add(new MappingValidationError(path, $"'{text}' must carry a uuid"));
                return new EscalationTarget(kind, value);
            }
        }
        errors.Add(new MappingValidationError(path, $"unknown target '{text}' (team_destinations | destination:<id> | user:<id> | external:<name>)"));
        return new EscalationTarget(TeamDestinations, null);
    }
}

public sealed record EscalationStep(TimeSpan After, IReadOnlyList<EscalationTarget> Targets);

/// <summary>Escalation policy document (04 §7.2). Timers use elapsed time; business-hours pause is an explicit flag (spec §7.3).</summary>
public sealed class EscalationPolicyDocument
{
    public int Version { get; init; }
    public TimeSpan? AckDeadline { get; init; }
    public TimeSpan? FollowUpWindow { get; init; }
    public required IReadOnlyList<EscalationStep> Steps { get; init; }
    public TimeSpan? RepeatLastStepEvery { get; init; }
    public int MaxRepeats { get; init; } = 3;
    public bool BusinessHoursOnly { get; init; }
    public required JsonObject Source { get; init; }

    public static EscalationPolicyDocument Parse(JsonNode root, int version)
    {
        var errors = new List<MappingValidationError>();
        if (root is not JsonObject top) throw new MappingValidationException([new MappingValidationError("$", "escalation policy must be an object")]);
        var doc = top["escalation"] as JsonObject ?? top;
        var basePath = ReferenceEquals(doc, top) ? "$" : "$.escalation";

        var steps = new List<EscalationStep>();
        if (doc["steps"] is JsonArray stepList)
        {
            for (var i = 0; i < stepList.Count; i++)
            {
                var path = $"{basePath}.steps[{i}]";
                if (stepList[i] is not JsonObject step)
                {
                    errors.Add(new MappingValidationError(path, "must be an object"));
                    continue;
                }
                var after = ParseDuration(step["after"], $"{path}.after", errors) ?? TimeSpan.Zero;
                var targets = new List<EscalationTarget>();
                if (step["targets"] is JsonArray targetList && targetList.Count > 0)
                {
                    foreach (var t in targetList) targets.Add(EscalationTarget.Parse(t?.ToString() ?? string.Empty, $"{path}.targets", errors));
                }
                else
                {
                    errors.Add(new MappingValidationError($"{path}.targets", "must be a non-empty list"));
                }
                steps.Add(new EscalationStep(after, targets));
            }
        }
        var maxRepeats = doc["max_repeats"] is JsonValue mr && MappingParser.TryGetInt(mr, out var m) ? m : 3;
        if (maxRepeats < 0) errors.Add(new MappingValidationError($"{basePath}.max_repeats", "must be ≥ 0"));
        if (errors.Count > 0) throw new MappingValidationException(errors);
        return new EscalationPolicyDocument
        {
            Version = version,
            AckDeadline = ParseDuration(doc["ack_deadline"], $"{basePath}.ack_deadline", errors),
            FollowUpWindow = ParseDuration(doc["follow_up_window"], $"{basePath}.follow_up_window", errors),
            Steps = steps.OrderBy(s => s.After).ToList(),
            RepeatLastStepEvery = ParseDuration(doc["repeat_last_step_every"], $"{basePath}.repeat_last_step_every", errors),
            MaxRepeats = maxRepeats,
            BusinessHoursOnly = doc["business_hours_only"] is JsonValue bh && bh.TryGetValue<bool>(out var b) && b,
            Source = (JsonObject)doc.DeepClone(),
        };
    }

    public static EscalationPolicyDocument ParseYaml(string yaml, int version)
        => Parse(YamlJson.Parse(yaml) ?? throw new MappingValidationException([new MappingValidationError("$", "document is empty")]), version);

    /// <summary>Durations: ISO 8601 (<c>PT15M</c>) or shorthand (<c>15m</c>, <c>2h</c>, <c>30s</c>, <c>7d</c>); <c>null</c> means none.</summary>
    public static TimeSpan? ParseDuration(JsonNode? node, string path, List<MappingValidationError> errors)
    {
        if (node is null) return null;
        var text = node.ToString().Trim();
        if (text.Length == 0 || text == "null") return null;
        if (text.StartsWith('P'))
        {
            try
            {
                return XmlConvert.ToTimeSpan(text);
            }
            catch (FormatException)
            {
                errors.Add(new MappingValidationError(path, $"invalid ISO 8601 duration '{text}'"));
                return null;
            }
        }
        var unit = text[^1];
        if (double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) && amount >= 0)
        {
            switch (unit)
            {
                case 's': return TimeSpan.FromSeconds(amount);
                case 'm': return TimeSpan.FromMinutes(amount);
                case 'h': return TimeSpan.FromHours(amount);
                case 'd': return TimeSpan.FromDays(amount);
            }
        }
        if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var ts)) return ts;
        errors.Add(new MappingValidationError(path, $"invalid duration '{text}' (use 15m, 2h, 30s, 7d or PT15M)"));
        return null;
    }
}
