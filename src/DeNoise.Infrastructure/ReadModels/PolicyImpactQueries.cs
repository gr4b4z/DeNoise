using DeNoise.Application.Policies;
using DeNoise.Domain.Episodes;
using DeNoise.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DeNoise.Infrastructure.ReadModels;

public sealed class PolicyImpactQueries(DeNoiseDbContext db) : IPolicyImpactQueries
{
    public async Task<IReadOnlyList<Episode>> OpenEpisodesByLifecyclePolicyAsync(Guid policyId, int limit, CancellationToken ct = default)
        => await db.Episodes.AsNoTracking().Where(e => e.LifecyclePolicyId == policyId && e.HandlingState != HandlingState.Closed).OrderBy(e => e.LastSeen).Take(limit).ToListAsync(ct);

    public Task<int> CountOpenByLifecyclePolicyAsync(Guid policyId, CancellationToken ct = default)
        => db.Episodes.CountAsync(e => e.LifecyclePolicyId == policyId && e.HandlingState != HandlingState.Closed, ct);

    public Task<int> CountOpenByRoutingRulesAsync(IReadOnlyCollection<Guid> ruleIds, CancellationToken ct = default)
        => db.Episodes.CountAsync(e => e.RoutingRuleId != null && ruleIds.Contains(e.RoutingRuleId.Value) && e.HandlingState != HandlingState.Closed, ct);

    public async Task<IReadOnlyList<Episode>> OpenEpisodesByRoutingRulesAsync(IReadOnlyCollection<Guid> ruleIds, int limit, CancellationToken ct = default)
        => await db.Episodes.AsNoTracking().Where(e => e.RoutingRuleId != null && ruleIds.Contains(e.RoutingRuleId.Value) && e.HandlingState != HandlingState.Closed).OrderBy(e => e.LastSeen).Take(limit).ToListAsync(ct);
}
