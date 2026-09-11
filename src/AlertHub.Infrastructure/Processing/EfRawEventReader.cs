using AlertHub.Application.Processing;
using AlertHub.Domain.Alerts;
using AlertHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlertHub.Infrastructure.Processing;

public sealed class EfRawEventReader(AlertHubDbContext db) : IRawEventReader
{
    public Task<RawEvent?> GetAsync(Guid eventId, DateTimeOffset receivedAt, CancellationToken ct = default)
        => db.RawEvents.AsNoTracking().SingleOrDefaultAsync(r => r.ReceivedAt == receivedAt && r.EventId == eventId, ct);
}
