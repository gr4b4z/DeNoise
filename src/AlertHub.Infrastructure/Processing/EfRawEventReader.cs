using AlertHub.Application.Processing;
using AlertHub.Domain.Alerts;
using AlertHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlertHub.Infrastructure.Processing;

public sealed class EfRawEventReader(AlertHubDbContext db) : IRawEventReader
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
