using DeNoise.Application.Notifications;
using DeNoise.Application.Policies;
using DeNoise.Application.Teams;
using DeNoise.Domain.Notifications;
using DeNoise.Domain.Policies;
using DeNoise.Domain.Teams;
using DeNoise.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DeNoise.Infrastructure.Notifications;

public sealed class EfTeamRepository(DeNoiseDbContext db) : ITeamRepository
{
    public Task<Team?> GetAsync(Guid teamId, CancellationToken ct = default) => db.Teams.AsNoTracking().SingleOrDefaultAsync(t => t.TeamId == teamId, ct);
    public async Task<IReadOnlyList<Team>> ListAsync(CancellationToken ct = default) => await db.Teams.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
    public Task<Team?> GetTriageAsync(CancellationToken ct = default) => db.Teams.AsNoTracking().SingleOrDefaultAsync(t => t.IsTriage, ct);
    public void Add(Team team) => db.Teams.Add(team);
}

public sealed class EfDestinationRepository(DeNoiseDbContext db) : IDestinationRepository
{
    public Task<Destination?> GetAsync(Guid destinationId, CancellationToken ct = default) => db.Destinations.AsNoTracking().SingleOrDefaultAsync(d => d.DestinationId == destinationId, ct);
    public async Task<IReadOnlyList<Destination>> ListAsync(CancellationToken ct = default) => await db.Destinations.AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct);
    public async Task<IReadOnlyList<Destination>> ListForTeamAsync(Guid teamId, CancellationToken ct = default) => await db.Destinations.AsNoTracking().Where(d => d.TeamId == teamId).OrderBy(d => d.Name).ToListAsync(ct);
    public Task<Destination?> GetTrackedAsync(Guid destinationId, CancellationToken ct = default) => db.Destinations.SingleOrDefaultAsync(d => d.DestinationId == destinationId, ct);
    public void Add(Destination destination) => db.Destinations.Add(destination);
}

public sealed class EfPolicyRepository(DeNoiseDbContext db) : IPolicyRepository
{
    public Task<PolicyVersion?> GetAsync(string kind, Guid policyId, int version, CancellationToken ct = default)
        => db.Policies.SingleOrDefaultAsync(p => p.Kind == kind && p.PolicyId == policyId && p.Version == version, ct);
    public Task<PolicyVersion?> GetActiveAsync(string kind, Guid policyId, CancellationToken ct = default)
        => db.Policies.SingleOrDefaultAsync(p => p.Kind == kind && p.PolicyId == policyId && p.ActivatedAt != null && p.DeactivatedAt == null, ct);
    public async Task<IReadOnlyList<PolicyVersion>> ListActiveAsync(string kind, CancellationToken ct = default)
        => await db.Policies.AsNoTracking().Where(p => p.Kind == kind && p.ActivatedAt != null && p.DeactivatedAt == null).ToListAsync(ct);
    public async Task<IReadOnlyList<PolicyVersion>> ListVersionsAsync(string kind, Guid policyId, CancellationToken ct = default)
        => await db.Policies.AsNoTracking().Where(p => p.Kind == kind && p.PolicyId == policyId).OrderByDescending(p => p.Version).ToListAsync(ct);
    public async Task<int> NextVersionAsync(string kind, Guid policyId, CancellationToken ct = default)
        => (await db.Policies.Where(p => p.Kind == kind && p.PolicyId == policyId).MaxAsync(p => (int?)p.Version, ct) ?? 0) + 1;
    public void Add(PolicyVersion version) => db.Policies.Add(version);
}
