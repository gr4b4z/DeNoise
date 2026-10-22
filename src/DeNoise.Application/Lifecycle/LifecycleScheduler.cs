using System.Text.Json;
using System.Text.Json.Nodes;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Policies;
using DeNoise.Application.Processing;
using DeNoise.Domain.Common;
using DeNoise.Domain.Episodes;
using DeNoise.Domain.Integrations;
using DeNoise.Domain.Ops;
using DeNoise.Domain.Policies;
using Microsoft.Extensions.Logging;

namespace DeNoise.Application.Lifecycle;

/// <summary>Payload of every lifecycle timer (<c>auto_resolve</c>, <c>stale_review</c>, <c>admin_expiry</c>, <c>informational_expiry</c>, <c>verify_state</c>).</summary>
public sealed record LifecycleJobPayload(Guid EpisodeId, Guid? PolicyId, int? PolicyVersion, string Profile, bool StaleEscalationSent = false);

/// <summary>The lifecycle that applies to one episode, and where it came from (shown in the explanation).</summary>
public sealed record EffectiveLifecycle(Guid? PolicyId, int? PolicyVersion, LifecyclePolicyDocument Document, string Source);

/// <summary>
/// Resolves the lifecycle for an episode (rule → integration defaults → mapping hint → type preset) and stages its timers
/// inside the processing transaction (04 §2.1 rows 1–2: schedule on open, reschedule on every effective signal).
/// </summary>
public sealed class LifecycleScheduler(IPolicyRepository policies, TimeProvider time, ILogger<LifecycleScheduler> logger)
{
    public static readonly IReadOnlyList<string> TimerKinds = [JobKinds.AutoResolve, JobKinds.VerifyState, JobKinds.StaleReview, JobKinds.AdminExpiry, JobKinds.InformationalExpiry];

    public async Task<EffectiveLifecycle> ResolveAsync(Episode episode, Integration integration, Guid? rulePolicyId, CancellationToken ct)
    {
        // 1. The routing rule that matched names a policy (04 §7.3 "per rule match"); on later signals the episode remembers it.
        var policyId = rulePolicyId ?? episode.LifecyclePolicyId;
        if (policyId is { } pid && await policies.GetActiveAsync(PolicyKinds.Lifecycle, pid, ct) is { } active)
        {
            try
            {
                return new EffectiveLifecycle(pid, active.Version, LifecyclePolicyDocument.Parse(JsonNode.Parse(active.Body)!, active.Version), $"lifecycle policy '{active.Name ?? pid.ToString()}' v{active.Version}");
            }
            catch (Mapping.MappingValidationException ex)
            {
                logger.LogError(ex, "Lifecycle policy {PolicyId} v{Version} does not parse; falling back to defaults", pid, active.Version);
            }
        }

        // 2. Integration-level defaults: either a policy reference or an inline document (spec §12.2 "per-source presets, all overridable").
        var defaults = ParseObject(integration.ProfileDefaults);
        if (defaults is not null)
        {
            if (defaults["lifecycle_policy"] is JsonValue lp && Guid.TryParse(lp.ToString(), out var lpid) && await policies.GetActiveAsync(PolicyKinds.Lifecycle, lpid, ct) is { } byIntegration)
            {
                return new EffectiveLifecycle(lpid, byIntegration.Version, LifecyclePolicyDocument.Parse(JsonNode.Parse(byIntegration.Body)!, byIntegration.Version), $"integration default policy v{byIntegration.Version}");
            }
            if (defaults["lifecycle"] is JsonObject || defaults["auto_resolve"] is JsonObject)
            {
                try
                {
                    var inline = defaults["lifecycle"] is JsonObject ? defaults : new JsonObject { ["lifecycle"] = new JsonObject { ["profile"] = episode.LifecycleProfile ?? LifecycleProfiles.DefaultFor(integration.Type) }, ["auto_resolve"] = defaults["auto_resolve"]?.DeepClone() };
                    return new EffectiveLifecycle(null, null, LifecyclePolicyDocument.Parse(inline, 0), "integration defaults");
                }
                catch (Mapping.MappingValidationException ex)
                {
                    logger.LogError(ex, "Integration {IntegrationId} profile defaults do not parse; using the profile preset", integration.IntegrationId);
                }
            }
        }

        // 3. Mapping hint, then the integration type preset (spec §12.2 table).
        if (LifecycleProfiles.IsInternal(episode.LifecycleProfile))
        {
            var internalDoc = LifecyclePolicyDocument.Preset(LifecycleProfiles.OneShot);
            return new EffectiveLifecycle(null, null, new LifecyclePolicyDocument
            {
                Version = 0,
                Profile = episode.LifecycleProfile!,
                DeliveryGrace = internalDoc.DeliveryGrace,
                MinInactivity = internalDoc.MinInactivity,
                Source = internalDoc.Source,
                AutoResolve = internalDoc.AutoResolve with { Enabled = false },
                Expiry = new ExpirySettings(null, null, null, internalDoc.Expiry.NeverExpireSeverities),
            }, $"internal profile '{episode.LifecycleProfile}': no lifecycle timers");
        }
        var profile = episode.LifecycleProfile is { } hint && LifecycleProfiles.All.Contains(hint) ? hint : LifecycleProfiles.DefaultFor(integration.Type);
        return new EffectiveLifecycle(null, null, LifecyclePolicyDocument.Preset(profile), $"preset for profile '{profile}'");
    }

    /// <summary>Re-stages every lifecycle timer from the current <c>last_seen</c>. Called on open and on every effective signal.</summary>
    public async Task ScheduleAsync(Episode episode, Integration integration, EffectiveLifecycle lifecycle, IProcessingSession session, DateTimeOffset now, CancellationToken ct, DateTimeOffset? earliestAutoResolve = null)
    {
        if (!episode.IsOpen) return;
        var doc = lifecycle.Document;
        await session.CancelJobsAsync(episode.EpisodeId, TimerKinds, ct);
        episode.LifecycleProfile = doc.Profile;
        episode.LifecyclePolicyId = lifecycle.PolicyId;
        episode.LifecyclePolicyVersion = lifecycle.PolicyVersion;
        episode.AutoResolveAt = null;
        var payload = new LifecycleJobPayload(episode.EpisodeId, lifecycle.PolicyId, lifecycle.PolicyVersion, doc.Profile);

        if (LifecycleProfiles.IsInternal(doc.Profile)) return; // coverage/heartbeat episodes resolve only through their own machines (04 §9, §10)

        if (!episode.IsActionable || episode.ConditionState == ConditionState.NotApplicable)
        {
            // Informational / one-shot: retention period, then closed as informational_completed (spec §12.5).
            if (doc.Expiry.InformationalAfter is { } after) Stage(session, episode, integration, JobKinds.InformationalExpiry, episode.LastSeen + after, payload, now);
            return;
        }

        if (doc.AutoResolve.Enabled && episode.ConditionState is ConditionState.Firing or ConditionState.Unknown)
        {
            var due = episode.LastSeen + doc.InactivityTimeout;
            if (due < now) due = now; // an old signal arriving late must not close instantly; the guards recompute anyway
            if (earliestAutoResolve is { } earliest && due < earliest) due = earliest; // policy activation: never close a backlog silently (spec §12.5)
            episode.AutoResolveAt = due;
            Stage(session, episode, integration, JobKinds.AutoResolve, due, payload, now);
        }

        // Administrative expiry (spec §12.5): never for critical/high/unknown severity; review first when the lifecycle is unknown.
        if (!doc.Expiry.NeverExpireSeverities.Contains(episode.Severity))
        {
            if (doc.Profile == LifecycleProfiles.Unknown && doc.Expiry.UnknownLifecycleReviewAfter is { } review)
            {
                Stage(session, episode, integration, JobKinds.StaleReview, episode.LastSeen + review, payload, now);
            }
            if (doc.Expiry.UnverifiedExpireAfter is { } expire)
            {
                Stage(session, episode, integration, JobKinds.AdminExpiry, episode.LastSeen + expire, payload, now);
            }
        }
    }

    public void Stage(IProcessingSession session, Episode episode, Integration integration, string kind, DateTimeOffset at, LifecycleJobPayload payload, DateTimeOffset now)
        => session.AddJob(new Job
        {
            JobId = Ids.New(time),
            Kind = kind,
            NotBefore = at,
            EpisodeId = episode.EpisodeId,
            IntegrationId = integration.IntegrationId,
            ExpectedVersion = episode.Version,
            ExpectedLastSeen = episode.LastSeen,
            Payload = JsonSerializer.Serialize(payload, JsonDefaults.Stored),
            CreatedAt = now,
            UpdatedAt = now,
        });

    private static JsonObject? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}") return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed class LifecyclePolicyValidator : IPolicyValidator
{
    public string Kind => PolicyKinds.Lifecycle;
    public void Validate(JsonNode body, int version) => LifecyclePolicyDocument.Parse(body, version);
}
