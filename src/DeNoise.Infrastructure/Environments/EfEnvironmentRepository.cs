using DeNoise.Application.Environments;
using DeNoise.Domain.Environments;
using DeNoise.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DeNoise.Infrastructure.Environments;

public sealed class EfEnvironmentRepository(DeNoiseDbContext db) : IEnvironmentRepository
{
    public async Task<IReadOnlyList<EnvironmentClass>> ListAsync(CancellationToken ct = default)
        => await db.Environments.AsNoTracking().OrderBy(e => e.SortOrder).ThenBy(e => e.Key).ToListAsync(ct);

    public Task<EnvironmentClass?> GetTrackedAsync(string key, CancellationToken ct = default) => db.Environments.SingleOrDefaultAsync(e => e.Key == key, ct);

    public void Add(EnvironmentClass environment) => db.Environments.Add(environment);

    public void Remove(EnvironmentClass environment) => db.Environments.Remove(environment);
}
