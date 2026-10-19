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

    public async Task<IReadOnlyList<Episode>> OpenEpisodesOpenedSinceAsync(DateTimeOffset since, int limit, CancellationToken ct = default)
        => await db.Episodes.AsNoTracking().Where(e => e.HandlingState != HandlingState.Closed && e.FirstSeen >= since).OrderByDescending(e => e.FirstSeen).Take(limit).ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, Domain.Alerts.NormalisedEvent>> LatestNormalisedEventsAsync(IReadOnlyCollection<Guid> episodeIds, CancellationToken ct = default)
    {
        if (episodeIds.Count == 0) return new Dictionary<Guid, Domain.Alerts.NormalisedEvent>();
        var ids = episodeIds.ToArray();
        var rows = await db.NormalisedEvents.FromSqlInterpolated($"SELECT DISTINCT ON (episode_id) * FROM alert.normalised_event WHERE episode_id = ANY({ids}) ORDER BY episode_id, received_at DESC").AsNoTracking().ToListAsync(ct);
        return rows.Where(r => r.EpisodeId is not null).ToDictionary(r => r.EpisodeId!.Value);
    }
}
