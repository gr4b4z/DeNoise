using System.Text.Json.Nodes;
using DeNoise.Application.Mapping;
using DeNoise.Application.Routing;
using DeNoise.Domain.Common;

namespace DeNoise.Application.Lifecycle;

/// <summary>Source profiles (spec §12.1). Presets are per profile; every field is operator-overridable in the policy.</summary>
public static class LifecycleProfiles
{
    public const string ExplicitRecovery = "explicit_recovery";
    public const string QueryableState = "queryable_state";
    public const string RepeatingWhileActive = "repeating_while_active";
    public const string OneShot = "one_shot";
    public const string Unknown = "unknown";
    /// <summary>Internal profile of coverage episodes: exempt from every lifecycle timer (04 §9).</summary>
    public const string Coverage = "coverage";
    /// <summary>Internal profile of heartbeat miss episodes: they resolve on the next ping with evidence <c>source</c>, never by inference (spec §13.3.3).</summary>
    public const string Heartbeat = "heartbeat";
    public static bool IsInternal(string? profile) => profile is Coverage or Heartbeat;
    public static readonly IReadOnlyList<string> All = [ExplicitRecovery, QueryableState, RepeatingWhileActive, OneShot, Unknown];

    /// <summary>Profile preset for an integration type when neither mapping nor policy names one (spec §12.2 table).</summary>
    public static string DefaultFor(string integrationType) => integrationType switch
    {
        Domain.Integrations.IntegrationTypes.AzureMonitor => ExplicitRecovery,
        Domain.Integrations.IntegrationTypes.Atlas => QueryableState,
        _ => Unknown,
    };
}

public static class VerifyBeforeClose
{
    public const string None = "none";
    public const string ApiIfAvailable = "api_if_available";
    public const string ApiRequired = "api_required";
    public static readonly IReadOnlyList<string> All = [None, ApiIfAvailable, ApiRequired];
}

public static class CoverageUnknownBehaviour
{
    public const string CloseUnverified = "close_unverified";
    public const string Suspend = "suspend";
    public static readonly IReadOnlyList<string> All = [CloseUnverified, Suspend];
}

/// <summary>Auto-resolve section (spec §12.3 YAML, 04 §7.3).</summary>
public sealed record AutoResolveSettings(
    bool Enabled,
    TimeSpan? AfterSilence,
    string VerifyBeforeClose,
    string OnCoverageUnknown,
    string OnCoverageDegraded,
    IReadOnlyList<Severity> EscalateBeforeCloseIfSeverity,
    TimeSpan StaleEscalationLead);

/// <summary>Administrative expiry section (spec §12.5).</summary>
public sealed record ExpirySettings(
    TimeSpan? InformationalAfter,
    TimeSpan? UnknownLifecycleReviewAfter,
    TimeSpan? UnverifiedExpireAfter,
    IReadOnlyList<Severity> NeverExpireSeverities);

/// <summary>
/// A lifecycle policy version (04 §7.3). Configured in the Hub per rule; the source is not involved (spec §12.2).
/// Missing fields fall back to the profile preset, so a policy of just <c>lifecycle: { profile: explicit_recovery }</c> is complete.
/// </summary>
public sealed class LifecyclePolicyDocument
{
    public int Version { get; init; }
    public required string Profile { get; init; }
    public TimeSpan? ExpectedRepeatInterval { get; init; }
    public TimeSpan DeliveryGrace { get; init; }
    public TimeSpan MinInactivity { get; init; }
    public required AutoResolveSettings AutoResolve { get; init; }
    public required ExpirySettings Expiry { get; init; }
    public required JsonObject Source { get; init; }

    /// <summary>The preset for a profile with nothing overridden.</summary>
    public static LifecyclePolicyDocument Preset(string profile, int version = 0)
        => Parse(new JsonObject { ["lifecycle"] = new JsonObject { ["profile"] = profile } }, version);

    /// <summary>
    /// Effective silence before inference (spec §12.3): explicit <c>after_silence</c> wins; otherwise
    /// <c>max(3 × expected_repeat_interval + delivery_grace, min_inactivity)</c>; a profile without an interval uses <c>min_inactivity</c>.
    /// </summary>
    public TimeSpan InactivityTimeout
    {
        get
        {
            if (AutoResolve.AfterSilence is { } explicitSilence) return explicitSilence;
            if (ExpectedRepeatInterval is { } interval && interval > TimeSpan.Zero)
            {
                var formula = interval * 3 + DeliveryGrace;
                return formula > MinInactivity ? formula : MinInactivity;
            }
            return MinInactivity;
        }
    }

    public static LifecyclePolicyDocument Parse(JsonNode root, int version)
    {
        var errors = new List<MappingValidationError>();
        if (root is not JsonObject top) throw new MappingValidationException([new MappingValidationError("$", "lifecycle policy must be an object")]);
        var lifecycle = top["lifecycle"] as JsonObject ?? top;
        var profile = lifecycle["profile"]?.ToString()?.Trim().ToLowerInvariant() ?? LifecycleProfiles.Unknown;
        if (!LifecycleProfiles.All.Contains(profile))
        {
            errors.Add(new MappingValidationError("$.lifecycle.profile", $"unknown profile '{profile}' (one of {string.Join(", ", LifecycleProfiles.All)})"));
            profile = LifecycleProfiles.Unknown;
        }
        var preset = PresetFor(profile);

        var interval = EscalationPolicyDocument.ParseDuration(lifecycle["expected_repeat_interval"], "$.lifecycle.expected_repeat_interval", errors) ?? preset.ExpectedRepeatInterval;
        var grace = EscalationPolicyDocument.ParseDuration(lifecycle["delivery_grace"], "$.lifecycle.delivery_grace", errors) ?? preset.DeliveryGrace;
        var minInactivity = EscalationPolicyDocument.ParseDuration(lifecycle["min_inactivity"], "$.lifecycle.min_inactivity", errors) ?? preset.MinInactivity;

        var ar = top["auto_resolve"] as JsonObject ?? lifecycle["auto_resolve"] as JsonObject;
        var enabled = ar?["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var en) ? en : preset.AutoResolve.Enabled;
        var afterSilence = EscalationPolicyDocument.ParseDuration(ar?["after_silence"], "$.auto_resolve.after_silence", errors) ?? preset.AutoResolve.AfterSilence;
        var verify = Enum(ar?["verify_before_close"], "$.auto_resolve.verify_before_close", VerifyBeforeClose.All, preset.AutoResolve.VerifyBeforeClose, errors);
        var onUnknown = Enum(ar?["on_coverage_unknown"], "$.auto_resolve.on_coverage_unknown", CoverageUnknownBehaviour.All, preset.AutoResolve.OnCoverageUnknown, errors);
        var onDegraded = Enum(ar?["on_coverage_degraded"], "$.auto_resolve.on_coverage_degraded", CoverageUnknownBehaviour.All, preset.AutoResolve.OnCoverageDegraded, errors);
        var escalateFor = Severities(ar?["escalate_before_close_if_severity"], "$.auto_resolve.escalate_before_close_if_severity", errors) ?? preset.AutoResolve.EscalateBeforeCloseIfSeverity;
        var lead = EscalationPolicyDocument.ParseDuration(ar?["stale_escalation_lead"], "$.auto_resolve.stale_escalation_lead", errors) ?? preset.AutoResolve.StaleEscalationLead;

        var ex = top["expiry"] as JsonObject ?? lifecycle["expiry"] as JsonObject;
        var informational = ex is null ? preset.Expiry.InformationalAfter : Optional(ex, "informational_after", "$.expiry.informational_after", errors, preset.Expiry.InformationalAfter);
        var review = ex is null ? preset.Expiry.UnknownLifecycleReviewAfter : Optional(ex, "unknown_lifecycle_review_after", "$.expiry.unknown_lifecycle_review_after", errors, preset.Expiry.UnknownLifecycleReviewAfter);
        var expire = ex is null ? preset.Expiry.UnverifiedExpireAfter : Optional(ex, "unverified_expire_after", "$.expiry.unverified_expire_after", errors, preset.Expiry.UnverifiedExpireAfter);
        var neverExpire = Severities(ex?["never_expire_severities"], "$.expiry.never_expire_severities", errors) ?? preset.Expiry.NeverExpireSeverities;

        if (errors.Count > 0) throw new MappingValidationException(errors);
        return new LifecyclePolicyDocument
        {
            Version = version,
            Profile = profile,
            ExpectedRepeatInterval = interval,
            DeliveryGrace = grace,
            MinInactivity = minInactivity,
            AutoResolve = new AutoResolveSettings(enabled, afterSilence, verify, onUnknown, onDegraded, escalateFor, lead),
            Expiry = new ExpirySettings(informational, review, expire, neverExpire),
            Source = (JsonObject)top.DeepClone(),
        };
    }

    public static LifecyclePolicyDocument ParseYaml(string yaml, int version)
        => Parse(YamlJson.Parse(yaml) ?? throw new MappingValidationException([new MappingValidationError("$", "document is empty")]), version);

    /// <summary>A key present with an explicit <c>null</c>/<c>none</c> disables the timer; absent keeps the preset.</summary>
    private static TimeSpan? Optional(JsonObject obj, string key, string path, List<MappingValidationError> errors, TimeSpan? preset)
    {
        if (!obj.ContainsKey(key)) return preset;
        var node = obj[key];
        if (node is null) return null;
        var text = node.ToString().Trim().ToLowerInvariant();
        if (text is "none" or "never" or "off" or "false") return null;
        return EscalationPolicyDocument.ParseDuration(node, path, errors);
    }

    private static string Enum(JsonNode? node, string path, IReadOnlyList<string> allowed, string preset, List<MappingValidationError> errors)
    {
        if (node is null) return preset;
        var value = node.ToString().Trim().ToLowerInvariant();
        if (allowed.Contains(value)) return value;
        errors.Add(new MappingValidationError(path, $"must be one of {string.Join(", ", allowed)}"));
        return preset;
    }

    private static IReadOnlyList<Severity>? Severities(JsonNode? node, string path, List<MappingValidationError> errors)
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
            if (text is null || !System.Enum.TryParse<Severity>(text, true, out var parsed) || !System.Enum.IsDefined(parsed))
            {
                errors.Add(new MappingValidationError(path, $"unknown severity '{text}'"));
                continue;
            }
            list.Add(parsed);
        }
        return list;
    }

    /// <summary>Presets per profile (spec §12.2 table and §12.5 defaults).</summary>
    private static LifecyclePolicyDocument PresetFor(string profile)
    {
        var grace = TimeSpan.FromMinutes(5);
        var min = TimeSpan.FromMinutes(15);
        var criticalHigh = new[] { Severity.Critical, Severity.High };
        var neverExpire = new[] { Severity.Critical, Severity.High, Severity.Unknown };
        var expiry = new ExpirySettings(TimeSpan.FromHours(24), TimeSpan.FromHours(24), TimeSpan.FromDays(7), neverExpire);
        var source = new JsonObject();
        return profile switch
        {
            LifecycleProfiles.ExplicitRecovery => new LifecyclePolicyDocument
            {
                Profile = profile,
                DeliveryGrace = grace,
                MinInactivity = min,
                Source = source,
                Expiry = expiry,
                // Backstop for a lost `resolved` webhook: 60 min of silence, state re-query where the API exists (spec §12.2).
                AutoResolve = new AutoResolveSettings(true, TimeSpan.FromMinutes(60), VerifyBeforeClose.ApiIfAvailable, CoverageUnknownBehaviour.CloseUnverified, CoverageUnknownBehaviour.Suspend, criticalHigh, TimeSpan.FromMinutes(30)),
            },
            LifecycleProfiles.QueryableState => new LifecyclePolicyDocument
            {
                Profile = profile,
                DeliveryGrace = grace,
                MinInactivity = min,
                Source = source,
                Expiry = expiry,
                AutoResolve = new AutoResolveSettings(true, TimeSpan.FromMinutes(30), VerifyBeforeClose.ApiIfAvailable, CoverageUnknownBehaviour.CloseUnverified, CoverageUnknownBehaviour.Suspend, criticalHigh, TimeSpan.FromMinutes(30)),
            },
            LifecycleProfiles.RepeatingWhileActive => new LifecyclePolicyDocument
            {
                Profile = profile,
                DeliveryGrace = grace,
                MinInactivity = min,
                Source = source,
                Expiry = expiry,
                AutoResolve = new AutoResolveSettings(true, null, VerifyBeforeClose.None, CoverageUnknownBehaviour.CloseUnverified, CoverageUnknownBehaviour.Suspend, criticalHigh, TimeSpan.FromMinutes(30)),
            },
            LifecycleProfiles.OneShot => new LifecyclePolicyDocument
            {
                Profile = profile,
                DeliveryGrace = grace,
                MinInactivity = min,
                Source = source,
                AutoResolve = new AutoResolveSettings(false, null, VerifyBeforeClose.None, CoverageUnknownBehaviour.CloseUnverified, CoverageUnknownBehaviour.Suspend, [], TimeSpan.FromMinutes(30)),
                Expiry = new ExpirySettings(TimeSpan.FromHours(24), null, null, neverExpire),
            },
            _ => new LifecyclePolicyDocument
            {
                Profile = LifecycleProfiles.Unknown,
                DeliveryGrace = grace,
                MinInactivity = min,
                Source = source,
                Expiry = expiry,
                // Generic webhook with no declared interval: 60 min, evidence is always inactivity_unverified (spec §12.2).
                AutoResolve = new AutoResolveSettings(true, TimeSpan.FromMinutes(60), VerifyBeforeClose.None, CoverageUnknownBehaviour.CloseUnverified, CoverageUnknownBehaviour.Suspend, criticalHigh, TimeSpan.FromMinutes(30)),
            },
        };
    }
}
