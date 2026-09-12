using DeNoise.Application.Suppressions;
using DeNoise.Domain.Policies;
using DeNoise.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DeNoise.Infrastructure.Policies;

public sealed class EfSuppressionRepository(DeNoiseDbContext db) : ISuppressionRepository
{
    public Task<Suppression?> GetAsync(Guid id, CancellationToken ct = default) => db.Suppressions.SingleOrDefaultAsync(s => s.SuppressionId == id, ct);

    public async Task<IReadOnlyList<Suppression>> ListAsync(bool includeEnded, DateTimeOffset now, CancellationToken ct = default)
        => await db.Suppressions.AsNoTracking()
            .Where(s => includeEnded || (s.CancelledAt == null && s.EndsAt > now))
            .OrderBy(s => s.StartsAt).ThenBy(s => s.EndsAt)
            .Take(1000)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Suppression>> ListActiveAsync(DateTimeOffset now, CancellationToken ct = default)
        => await db.Suppressions.AsNoTracking().Where(s => s.CancelledAt == null && s.StartsAt <= now && s.EndsAt > now).ToListAsync(ct);

    public void Add(Suppression suppression) => db.Suppressions.Add(suppression);
}
