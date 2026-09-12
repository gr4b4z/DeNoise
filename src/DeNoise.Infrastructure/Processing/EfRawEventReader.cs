using DeNoise.Application.Processing;
using DeNoise.Domain.Alerts;
using DeNoise.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DeNoise.Infrastructure.Processing;

public sealed class EfRawEventReader(DeNoiseDbContext db) : IRawEventReader
{
    public Task<RawEvent?> GetAsync(Guid eventId, DateTimeOffset receivedAt, CancellationToken ct = default)
        => db.RawEvents.AsNoTracking().SingleOrDefaultAsync(r => r.ReceivedAt == receivedAt && r.EventId == eventId, ct);

    public Task<RawEvent?> FindAsync(Guid integrationId, Guid eventId, CancellationToken ct = default)
        => db.RawEvents.AsNoTracking().FirstOrDefaultAsync(r => r.IntegrationId == integrationId && r.EventId == eventId, ct);

    public async Task<IReadOnlyList<(Guid EventId, DateTimeOffset ReceivedAt)>> ListAsync(Guid integrationId, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken ct = default)
    {
        var rows = await db.RawEvents.AsNoTracking()
            .Where(r => r.IntegrationId == integrationId && r.ReceivedAt >= from && r.ReceivedAt < to)
            .OrderBy(r => r.ReceivedAt).ThenBy(r => r.EventId)
            .Take(limit)
            .Select(r => new { r.EventId, r.ReceivedAt })
            .ToListAsync(ct);
        return rows.Select(r => (r.EventId, r.ReceivedAt)).ToList();
    }
}
