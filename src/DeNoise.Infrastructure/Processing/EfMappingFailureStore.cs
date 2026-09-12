using DeNoise.Application.Replay;
using DeNoise.Domain.Alerts;
using DeNoise.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DeNoise.Infrastructure.Processing;

/// <summary><c>alert.mapping_failure</c> for the failures screen and the replay job; writes are committed immediately (they are not part of a processing transaction).</summary>
public sealed class EfMappingFailureStore(DeNoiseDbContext db) : IMappingFailureStore
{
    public async Task<IReadOnlyList<MappingFailure>> ListAsync(Guid integrationId, bool quarantinedOnly, int limit, CancellationToken ct = default)
        => await db.MappingFailures.AsNoTracking()
            .Where(f => f.IntegrationId == integrationId && (!quarantinedOnly || f.Quarantined))
            .OrderByDescending(f => f.RawReceivedAt)
            .Take(Math.Clamp(limit, 1, 50_001))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<MappingFailure>> GetByEventsAsync(Guid integrationId, IReadOnlyCollection<Guid> eventIds, CancellationToken ct = default)
    {
        var ids = eventIds.ToArray();
        return await db.MappingFailures.AsNoTracking().Where(f => f.IntegrationId == integrationId && ids.Contains(f.EventId)).ToListAsync(ct);
    }

    public Task<int> ResolveAsync(IReadOnlyCollection<Guid> eventIds, DateTimeOffset at, Guid? by, CancellationToken ct = default)
    {
        var ids = eventIds.ToArray();
        return db.MappingFailures.Where(f => ids.Contains(f.EventId) && f.Quarantined)
            .ExecuteUpdateAsync(u => u.SetProperty(f => f.Quarantined, false).SetProperty(f => f.ResolvedAt, at).SetProperty(f => f.ResolvedBy, by), ct);
    }

    public async Task<bool> DismissAsync(Guid failureId, DateTimeOffset at, Guid? by, CancellationToken ct = default)
        => await db.MappingFailures.Where(f => f.Id == failureId && f.Quarantined)
            .ExecuteUpdateAsync(u => u.SetProperty(f => f.Quarantined, false).SetProperty(f => f.ResolvedAt, at).SetProperty(f => f.ResolvedBy, by), ct) > 0;
}
