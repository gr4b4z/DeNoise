using System.Text.Json.Nodes;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Lifecycle;
using AlertHub.Application.Processing;
using AlertHub.Application.Routing;
using AlertHub.Domain.Common;
using AlertHub.Domain.Episodes;
using AlertHub.Domain.Policies;
using Microsoft.Extensions.Logging;

namespace AlertHub.Application.Policies;

/// <summary>Read side of the impact preview: open episodes a policy version would touch (06 §4 <c>/impact</c>).</summary>
public interface IPolicyImpactQueries
{
    Task<IReadOnlyList<Episode>> OpenEpisodesByLifecyclePolicyAsync(Guid policyId, int limit, CancellationToken ct = default);
    Task<int> CountOpenByLifecyclePolicyAsync(Guid policyId, CancellationToken ct = default);
    Task<int> CountOpenByRoutingRulesAsync(IReadOnlyCollection<Guid> ruleIds, CancellationToken ct = default);
    Task<IReadOnlyList<Episode>> OpenEpisodesByRoutingRulesAsync(IReadOnlyCollection<Guid> ruleIds, int limit, CancellationToken ct = default);
}

public sealed record PolicyImpactSample(Guid EpisodeId, string? Summary, string Severity, DateTimeOffset LastSeen, DateTimeOffset? CurrentAutoResolveAt, DateTimeOffset? ProposedAutoResolveAt, string Note);
public sealed record PolicyImpact(string Kind, Guid PolicyId, int Version, int AffectedOpenEpisodes, IReadOnlyList<PolicyImpactSample> Sample, string Explanation);

/// <summary>
/// "Operator changes an auto-resolve timeout" (spec §22): before activation, show how many open episodes the version affects.
/// Lifecycle: every open episode on the policy, with its recomputed deadline. Routing: open episodes whose matched rule changed team or disappeared.
/// </summary>
public sealed class PolicyImpactService(IPolicyRepository policies, IPolicyImpactQueries queries, TimeProvider time)
{
    public async Task<PolicyImpact> PreviewAsync(string kind, Guid policyId, int version, CancellationToken ct = default)
    {
        var target = await policies.GetAsync(kind, policyId, version, ct) ?? throw new KeyNotFoundException($"{kind} policy {policyId} v{version} not found.");
        var now = time.GetUtcNow();
        switch (kind)
        {
            case PolicyKinds.Lifecycle:
                {
                    var doc = LifecyclePolicyDocument.Parse(JsonNode.Parse(target.Body)!, version);
                    var count = await queries.CountOpenByLifecyclePolicyAsync(policyId, ct);
                    var sample = (await queries.OpenEpisodesByLifecyclePolicyAsync(policyId, 10, ct)).Select(e =>
                    {
                        DateTimeOffset? proposed = doc.AutoResolve.Enabled && e.IsActionable ? Max(e.LastSeen + doc.InactivityTimeout, now + TimeSpan.FromMinutes(1)) : null;
                        var note = proposed is null ? "automatic resolution would be disabled" : proposed < e.AutoResolveAt ? "would resolve earlier" : proposed > e.AutoResolveAt ? "would resolve later" : "unchanged";
                        return new PolicyImpactSample(e.EpisodeId, e.Summary, e.Severity.ToWire(), e.LastSeen, e.AutoResolveAt, proposed, note);
                    }).ToList();
                    var explanation = $"{count} open episode(s) use this lifecycle policy. Activation reschedules their timers from last_seen with the new timeout ({doc.InactivityTimeout}); nothing closes at activation — the earliest new deadline is one minute out and every guard still applies.";
                    return new PolicyImpact(kind, policyId, version, count, sample, explanation);
                }
            case PolicyKinds.Routing:
                {
                    var proposed = RoutingPolicyDocument.Parse(JsonNode.Parse(target.Body)!, version);
                    var active = await policies.GetActiveAsync(kind, policyId, ct);
                    var current = active is null ? null : RoutingPolicyDocument.Parse(JsonNode.Parse(active.Body)!, active.Version);
                    var proposedRules = proposed.Rules.ToDictionary(r => r.RuleId);
                    var changed = (current?.Rules ?? []).Where(r => !proposedRules.TryGetValue(r.RuleId, out var p) || p.TeamId != r.TeamId || p.EscalationPolicyId != r.EscalationPolicyId || p.LifecyclePolicyId != r.LifecyclePolicyId)
                        .Select(r => r.RuleId).ToList();
                    var count = changed.Count == 0 ? 0 : await queries.CountOpenByRoutingRulesAsync(changed, ct);
                    var sample = changed.Count == 0 ? [] : (await queries.OpenEpisodesByRoutingRulesAsync(changed, 10, ct))
                        .Select(e => new PolicyImpactSample(e.EpisodeId, e.Summary, e.Severity.ToWire(), e.LastSeen, e.AutoResolveAt, e.AutoResolveAt, "matched a rule that this version changes or removes; existing ownership is kept, new episodes route by the new rules")).ToList();
                    return new PolicyImpact(kind, policyId, version, count, sample, $"{changed.Count} rule(s) change team, escalation or lifecycle; {count} open episode(s) were routed by them. Activation never re-routes open episodes.");
                }
            default:
                return new PolicyImpact(kind, policyId, version, 0, [], "Impact preview applies to lifecycle and routing policies.");
        }
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}

/// <summary>Runs after a policy version is activated (post-commit). Lifecycle activation reschedules the timers of affected open episodes.</summary>
public interface IPolicyActivationHook
{
    Task AfterActivatedAsync(string kind, Guid policyId, int version, Actor actor, CancellationToken ct);
}

/// <summary>
/// Activation of a lifecycle version reschedules <c>auto_resolve</c>/expiry timers of every open episode on the policy from its
/// <c>last_seen</c>; the earliest new deadline is one minute out so a backlog is never closed silently (spec §12.5).
/// </summary>
public sealed class LifecycleActivationHook(IProcessingUnitOfWork uow, LifecycleScheduler scheduler, Integrations.IIntegrationRepository integrations, TimeProvider time, ILogger<LifecycleActivationHook> logger) : IPolicyActivationHook
{
    public async Task AfterActivatedAsync(string kind, Guid policyId, int version, Actor actor, CancellationToken ct)
    {
        if (kind != PolicyKinds.Lifecycle) return;
        var rescheduled = await uow.RunAsync(async session =>
        {
            var now = time.GetUtcNow();
            var count = 0;
            foreach (var episode in await session.ListOpenEpisodesByLifecyclePolicyForUpdateAsync(policyId, ct))
            {
                var integration = await integrations.GetCurrentAsync(episode.IntegrationId, ct);
                if (integration is null) continue;
                var effective = await scheduler.ResolveAsync(episode, integration, policyId, ct);
                // Never close a backlog at activation: every rescheduled deadline is at least a minute out, the guards decide after that.
                await scheduler.ScheduleAsync(episode, integration, effective, session, now, ct, earliestAutoResolve: now + TimeSpan.FromMinutes(1));
                session.AddEpisodeEvent(new EpisodeEvent
                {
                    Id = Domain.Common.Ids.New(time),
                    EpisodeId = episode.EpisodeId,
                    At = now,
                    Kind = EpisodeEventKind.Postpone,
                    Detail = System.Text.Json.JsonSerializer.Serialize(new { reason = "policy_activated", policyId, version, autoResolveAt = episode.AutoResolveAt, actor = actor.Display ?? actor.Id }, JsonDefaults.Stored),
                });
                count++;
            }
            return count;
        }, ct);
        logger.LogInformation("Lifecycle policy {PolicyId} v{Version} activated; {Count} open episode(s) rescheduled", policyId, version, rescheduled);
    }
}
