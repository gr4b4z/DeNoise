using AlertHub.Application.Audit;
using AlertHub.Domain.Audit;
using AlertHub.Infrastructure.Persistence;

namespace AlertHub.Infrastructure.Audit;

public sealed class EfAuditWriter(AlertHubDbContext db) : IAuditWriter
{
    public void Record(AuditEntry entry) => db.AuditEntries.Add(entry);
}
