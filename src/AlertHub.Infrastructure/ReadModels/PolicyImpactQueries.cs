using AlertHub.Application.Policies;
using AlertHub.Domain.Episodes;
using AlertHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlertHub.Infrastructure.ReadModels;

public sealed class PolicyImpactQueries(AlertHubDbContext db) : IPolicyImpactQueries
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
