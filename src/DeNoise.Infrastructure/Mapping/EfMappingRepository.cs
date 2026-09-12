using System.Text.Json.Nodes;
using DeNoise.Application.Mapping;
using DeNoise.Application.Processing;
using DeNoise.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace DeNoise.Infrastructure.Mapping;

public sealed class EfMappingRepository(DeNoiseDbContext db) : IMappingRepository
{
    public async Task<IReadOnlyList<MappingVersion>> ListAsync(Guid integrationId, CancellationToken ct = default)
        => await db.MappingVersions.AsNoTracking().Where(m => m.IntegrationId == integrationId).OrderBy(m => m.Order).ThenBy(m => m.MappingId).ThenByDescending(m => m.Version).ToListAsync(ct);

    public Task<MappingVersion?> GetAsync(Guid mappingId, int version, CancellationToken ct = default)
        => db.MappingVersions.SingleOrDefaultAsync(m => m.MappingId == mappingId && m.Version == version, ct);

    public Task<MappingVersion?> GetActiveAsync(Guid mappingId, CancellationToken ct = default)
        => db.MappingVersions.SingleOrDefaultAsync(m => m.MappingId == mappingId && m.ActivatedAt != null && m.DeactivatedAt == null, ct);

    public async Task<int> NextVersionAsync(Guid mappingId, CancellationToken ct = default)
        => (await db.MappingVersions.Where(m => m.MappingId == mappingId).MaxAsync(m => (int?)m.Version, ct) ?? 0) + 1;

    public void Add(MappingVersion version) => db.MappingVersions.Add(version);
}

/// <summary>
/// Active mapping documents per integration, parsed once per (mapping id, version) and cached; the list itself
/// is cached briefly so activation propagates within seconds without a restart.
/// </summary>
public sealed class CachedMappingResolver(DeNoiseDbContext db, IMemoryCache cache) : IMappingResolver
{
    public static readonly TimeSpan ListTtl = TimeSpan.FromSeconds(15);

    public async Task<IReadOnlyList<MappingDocument>> GetActiveAsync(Guid integrationId, CancellationToken ct = default)
    {
        var listKey = $"mappings:active:{integrationId}";
        if (cache.TryGetValue(listKey, out IReadOnlyList<MappingDocument>? cached) && cached is not null) return cached;

        var rows = await db.MappingVersions.AsNoTracking()
            .Where(m => m.IntegrationId == integrationId && m.ActivatedAt != null && m.DeactivatedAt == null)
            .OrderBy(m => m.Order).ThenBy(m => m.MappingId)
            .Select(m => new { m.MappingId, m.Version, m.Body })
            .ToListAsync(ct);

        var docs = new List<MappingDocument>(rows.Count);
        foreach (var row in rows)
        {
            var docKey = $"mappings:doc:{row.MappingId}:{row.Version}";
            var doc = cache.GetOrCreate(docKey, entry =>
            {
                entry.SlidingExpiration = TimeSpan.FromHours(1);
                return MappingParser.Parse(JsonNode.Parse(row.Body)!, row.Version);
            })!;
            docs.Add(doc);
        }
        cache.Set(listKey, (IReadOnlyList<MappingDocument>)docs, ListTtl);
        return docs;
    }

    public void Invalidate(Guid integrationId) => cache.Remove($"mappings:active:{integrationId}");
}
