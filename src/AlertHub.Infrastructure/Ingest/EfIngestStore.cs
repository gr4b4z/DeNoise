using AlertHub.Application.Ingest;
using AlertHub.Domain.Alerts;
using AlertHub.Domain.Ops;
using AlertHub.Infrastructure.Persistence;

namespace AlertHub.Infrastructure.Ingest;

/// <summary>Raw event and normalise job in one <c>SaveChanges</c>, i.e. one transaction. No 2xx before this returns.</summary>
public sealed class EfIngestStore(AlertHubDbContext db) : IIngestStore
{
    public Task AcceptAsync(RawEvent rawEvent, Job normaliseJob, CancellationToken ct = default)
    {
        db.RawEvents.Add(rawEvent);
        db.Jobs.Add(normaliseJob);
        return db.SaveChangesAsync(ct);
    }
}
