using System.Text.Json.Nodes;
using DeNoise.Application.Mapping;
using DeNoise.Application.Routing;

namespace DeNoise.Application.Coverage;

/// <summary>Coverage methods (spec §13.5 YAML). Milestone 5 evaluates <c>managed_canary</c>; heartbeats bind in milestone 6, probes in 8.</summary>
public static class CoverageMethodTypes
{
    public const string ManagedCanary = "managed_canary";
    public const string RegisteredHeartbeat = "registered_heartbeat";
    public const string ApiProbe = "api_probe";
}

public sealed record CanaryMethod(TimeSpan ExpectedInterval, TimeSpan DelayedAfter, TimeSpan AlertAfter, TimeSpan UnavailableAfter, int RecoverySuccessesRequired);

/// <summary>Parsed <c>cfg.integration.coverage</c>. <see cref="Canary"/> is null when no canary method is configured (coverage stays <c>unknown</c>, spec §12.4).</summary>
/// <summary>Periodic reachability check of the source API (spec §13.5): supporting evidence, degrades coverage on failure but never raises it.</summary>
public sealed record ApiProbeMethod(TimeSpan Interval);

public sealed record CoverageConfig(CanaryMethod? Canary, IReadOnlyList<string> HeartbeatIds, bool SuspendInactivityResolution, ApiProbeMethod? ApiProbe = null)
{
    public static readonly CoverageConfig None = new(null, [], true);

    public bool HasMethods => Canary is not null || HeartbeatIds.Count > 0;

    public static CoverageConfig Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}") return None;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return None;
        }
        return root is JsonObject obj ? Parse(obj) : None;
    }

    public static CoverageConfig Parse(JsonObject root)
    {
        var errors = new List<MappingValidationError>();
        var coverage = root["coverage"] as JsonObject ?? root;
        CanaryMethod? canary = null;
        var heartbeats = new List<string>();
        ApiProbeMethod? apiProbe = null;
        if (coverage["methods"] is JsonArray methods)
        {
            for (var i = 0; i < methods.Count; i++)
            {
                if (methods[i] is not JsonObject m) continue;
                var path = $"$.coverage.methods[{i}]";
                switch (m["type"]?.ToString())
                {
                    case CoverageMethodTypes.ManagedCanary:
                        {
                            var expected = EscalationPolicyDocument.ParseDuration(m["expected_interval"], $"{path}.expected_interval", errors) ?? TimeSpan.FromMinutes(5);
                            var delayed = EscalationPolicyDocument.ParseDuration(m["delayed_after"], $"{path}.delayed_after", errors) ?? expected * 2;
                            var alert = EscalationPolicyDocument.ParseDuration(m["alert_after"], $"{path}.alert_after", errors) ?? expected * 3;
                            var unavailable = EscalationPolicyDocument.ParseDuration(m["unavailable_after"], $"{path}.unavailable_after", errors) ?? expected * 6;
                            var required = m["recovery_successes_required"] is JsonValue rv && MappingParser.TryGetInt(rv, out var r) && r > 0 ? r : 3;
                            if (delayed > alert || alert > unavailable) errors.Add(new MappingValidationError(path, "expected delayed_after ≤ alert_after ≤ unavailable_after"));
                            canary = new CanaryMethod(expected, delayed, alert, unavailable, required);
                            break;
                        }
                    case CoverageMethodTypes.RegisteredHeartbeat:
                        if (m["heartbeat_id"]?.ToString() is { Length: > 0 } hb) heartbeats.Add(hb);
                        break;
                    case CoverageMethodTypes.ApiProbe:
                        {
                            // supporting only (spec §13.5); never raises coverage above degraded on its own
                            var interval = m["interval"] is JsonValue iv && TimeSpan.TryParse(iv.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var ts) && ts >= TimeSpan.FromMinutes(1) ? ts : TimeSpan.FromMinutes(5);
                            apiProbe = new ApiProbeMethod(interval);
                            break;
                        }
                    default:
                        errors.Add(new MappingValidationError($"{path}.type", "must be managed_canary, registered_heartbeat or api_probe"));
                        break;
                }
            }
        }
        var suspend = root["on_failure"] is not JsonObject of || of["suspend_inactivity_resolution"] is not JsonValue sv || !sv.TryGetValue<bool>(out var s) || s;
        if (errors.Count > 0) throw new MappingValidationException(errors);
        return new CoverageConfig(canary, heartbeats, suspend, apiProbe);
    }
}
