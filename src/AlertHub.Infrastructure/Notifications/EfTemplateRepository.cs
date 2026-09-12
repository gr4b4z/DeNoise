using AlertHub.Application.Notifications.Templates;
using AlertHub.Domain.Notifications;
using AlertHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlertHub.Infrastructure.Notifications;

public sealed class EfTemplateRepository(AlertHubDbContext db) : ITemplateRepository
{
    public Task<WebhookTemplate?> GetAsync(Guid templateId, int version, CancellationToken ct = default) => db.WebhookTemplates.SingleOrDefaultAsync(t => t.TemplateId == templateId && t.Version == version, ct);
    public Task<WebhookTemplate?> GetActiveAsync(Guid templateId, CancellationToken ct = default) => db.WebhookTemplates.SingleOrDefaultAsync(t => t.TemplateId == templateId && t.ActivatedAt != null && t.DeactivatedAt == null, ct);
    public async Task<IReadOnlyList<WebhookTemplate>> ListActiveAsync(CancellationToken ct = default) => await db.WebhookTemplates.AsNoTracking().Where(t => t.ActivatedAt != null && t.DeactivatedAt == null).OrderByDescending(t => t.Builtin).ThenBy(t => t.Name).ToListAsync(ct);
    public async Task<IReadOnlyList<WebhookTemplate>> ListVersionsAsync(Guid templateId, CancellationToken ct = default) => await db.WebhookTemplates.AsNoTracking().Where(t => t.TemplateId == templateId).OrderBy(t => t.Version).ToListAsync(ct);
    public async Task<int> NextVersionAsync(Guid templateId, CancellationToken ct = default) => (await db.WebhookTemplates.Where(t => t.TemplateId == templateId).MaxAsync(t => (int?)t.Version, ct) ?? 0) + 1;
    public void Add(WebhookTemplate template) => db.WebhookTemplates.Add(template);
    public Task<string?> LatestPayloadForEpisodeAsync(Guid episodeId, CancellationToken ct = default)
        => db.Outbox.AsNoTracking().Where(o => o.EpisodeId == episodeId).OrderByDescending(o => o.CreatedAt).Select(o => o.Payload).FirstOrDefaultAsync(ct);
}
