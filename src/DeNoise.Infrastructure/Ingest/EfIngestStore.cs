using DeNoise.Application.Ingest;
using DeNoise.Domain.Alerts;
using DeNoise.Domain.Ops;
using DeNoise.Infrastructure.Persistence;

namespace DeNoise.Infrastructure.Ingest;

/// <summary>Raw event and normalise job in one <c>SaveChanges</c>, i.e. one transaction. No 2xx before this returns.</summary>
public sealed class EfIngestStore(DeNoiseDbContext db) : IIngestStore
{
    public Task AcceptAsync(RawEvent rawEvent, Job normaliseJob, CancellationToken ct = default)
    {
        db.RawEvents.Add(rawEvent);
        db.Jobs.Add(normaliseJob);
        return db.SaveChangesAsync(ct);
    }
}
