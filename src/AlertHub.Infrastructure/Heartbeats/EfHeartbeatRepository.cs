using AlertHub.Application.Heartbeats;
using AlertHub.Domain.Heartbeats;
using AlertHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlertHub.Infrastructure.Heartbeats;

public sealed class EfHeartbeatRepository(AlertHubDbContext db) : IHeartbeatRepository
{
    public Task<Heartbeat?> GetAsync(Guid heartbeatId, CancellationToken ct = default) => db.Heartbeats.SingleOrDefaultAsync(h => h.HeartbeatId == heartbeatId, ct);

    public Task<Heartbeat?> FindByKeyIdAsync(string keyId, CancellationToken ct = default) => db.Heartbeats.AsNoTracking().SingleOrDefaultAsync(h => h.KeyId == keyId, ct);

    public async Task<IReadOnlyList<Heartbeat>> ListAsync(HeartbeatFilter filter, CancellationToken ct = default)
    {
        var q = db.Heartbeats.AsNoTracking().AsQueryable();
        if (filter.State is { Length: > 0 } state) q = q.Where(h => h.State == state);
        if (filter.TeamId is { } team) q = q.Where(h => h.OwningTeamId == team);
        if (filter.Scope is { Length: > 0 } scope) q = q.Where(h => h.AccessScope == scope);
        if (filter.IntegrationId is { } integration) q = q.Where(h => h.BindsToIntegrationId == integration);
        if (filter.Query is { Length: > 0 } text) q = q.Where(h => EF.Functions.ILike(h.Name, $"%{text}%"));
        return await q.OrderBy(h => h.Name).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Heartbeat>> ListBoundToAsync(Guid integrationId, CancellationToken ct = default)
        => await db.Heartbeats.AsNoTracking().Where(h => h.BindsToIntegrationId == integrationId).ToListAsync(ct);

    public async Task<IReadOnlyList<HeartbeatRun>> ListRunsAsync(Guid heartbeatId, int limit, CancellationToken ct = default)
        => await db.HeartbeatRuns.AsNoTracking().Where(r => r.HeartbeatId == heartbeatId).OrderByDescending(r => r.Seq).Take(limit).ToListAsync(ct);

    public void Add(Heartbeat heartbeat) => db.Heartbeats.Add(heartbeat);
    public void Remove(Heartbeat heartbeat) => db.Heartbeats.Remove(heartbeat);
}
