using DeNoise.Application.Audit;
using DeNoise.Domain.Audit;
using DeNoise.Infrastructure.Persistence;

namespace DeNoise.Infrastructure.Audit;

public sealed class EfAuditWriter(DeNoiseDbContext db) : IAuditWriter
{
    public void Record(AuditEntry entry) => db.AuditEntries.Add(entry);
}
