using DeNoise.Application.Integrations;
using DeNoise.Domain.Integrations;
using DeNoise.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DeNoise.Infrastructure.Integrations;

public sealed class EfIntegrationRepository(DeNoiseDbContext db) : IIntegrationRepository
{
    public Task<Integration?> GetCurrentAsync(Guid integrationId, CancellationToken ct = default)
        => db.Integrations.SingleOrDefaultAsync(i => i.IntegrationId == integrationId && i.DeactivatedAt == null, ct);

    public Task<Integration?> FindCurrentByIngestKeyAsync(string ingestKeyId, CancellationToken ct = default)
        => db.Integrations.AsNoTracking().SingleOrDefaultAsync(i => i.IngestKeyId == ingestKeyId && i.DeactivatedAt == null, ct);

    public async Task<IReadOnlyList<Integration>> ListCurrentAsync(CancellationToken ct = default)
        => await db.Integrations.AsNoTracking().Where(i => i.DeactivatedAt == null).OrderBy(i => i.Name).ToListAsync(ct);

    public void Add(Integration version) => db.Integrations.Add(version);
}
