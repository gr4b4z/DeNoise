using AlertHub.Application.Coverage;
using AlertHub.Contracts;
using AlertHub.Domain.Episodes;
using AlertHub.Domain.Integrations;
using AlertHub.Domain.Ops;
using AlertHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AlertHub.Infrastructure.ReadModels;

/// <summary>Integration health (06 §4 <c>/integrations/{id}/health</c>) and hub health (<c>/hub/health</c>, spec §13.7) read models.</summary>
public sealed class HealthQueries(AlertHubDbContext db, IConfiguration configuration, TimeProvider time)
{
    public static readonly TimeSpan ComponentStaleAfter = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan DeadmanStaleAfter = TimeSpan.FromMinutes(3);

    public async Task<IntegrationHealth> IntegrationAsync(Integration integration, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var since = now - TimeSpan.FromMinutes(15);
        var id = integration.IntegrationId;
        var coverage = await db.CoverageStates.AsNoTracking().SingleOrDefaultAsync(c => c.IntegrationId == id, ct);
        var config = CoverageConfig.Parse(integration.Coverage);
        var lastProcessed = await db.NormalisedEvents.AsNoTracking().Where(e => e.IntegrationId == id && e.EventType != Domain.Alerts.EventTypes.Heartbeat).MaxAsync(e => (DateTimeOffset?)e.ReceivedAt, ct);
        var accepted = await db.RawEvents.AsNoTracking().CountAsync(r => r.IntegrationId == id && r.ReceivedAt >= since, ct);
        var failures = await db.MappingFailures.AsNoTracking().CountAsync(f => f.IntegrationId == id && f.RawReceivedAt >= since, ct);
        var pending = db.Jobs.AsNoTracking().Where(j => j.IntegrationId == id && j.Kind == JobKinds.Normalise && (j.Status == JobStatus.Pending || j.Status == JobStatus.Reserved));
        var pendingCount = await pending.CountAsync(ct);
        var oldest = pendingCount == 0 ? null : await pending.MinAsync(j => (DateTimeOffset?)j.CreatedAt, ct);
        var suspended = await db.Jobs.AsNoTracking().CountAsync(j => j.IntegrationId == id && j.Kind == JobKinds.AutoResolve && j.Status == JobStatus.Suspended, ct);
        var open = await db.Episodes.AsNoTracking().CountAsync(e => e.IntegrationId == id && e.HandlingState != HandlingState.Closed, ct);
        return new IntegrationHealth(id, integration.Name, config.HasMethods, coverage?.State ?? (config.HasMethods ? CoverageStates.Unknown : CoverageStates.NotConfigured), coverage?.Since, coverage?.LastSignalAt,
            coverage?.ConsecutiveSuccesses ?? 0, coverage?.CoverageEpisodeId, lastProcessed, accepted, failures, pendingCount, oldest, suspended, open);
    }

    public async Task<HubHealth> HubAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var dbOk = await db.Database.CanConnectAsync(ct);
        var components = (await db.HubComponentHeartbeats.AsNoTracking().OrderBy(h => h.Component).ToListAsync(ct))
            .Where(h => h.Component != "deadman")
            .Select(h => new HubComponent(h.Component, h.Instance, h.LastSeen, now - h.LastSeen <= ComponentStaleAfter)).ToList();
        var queues = (await db.Jobs.AsNoTracking()
                .Where(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Reserved || j.Status == JobStatus.Suspended || j.Status == JobStatus.Failed)
                .GroupBy(j => j.Kind)
                .Select(g => new
                {
                    Kind = g.Key,
                    Pending = g.Count(j => j.Status == JobStatus.Pending),
                    Reserved = g.Count(j => j.Status == JobStatus.Reserved),
                    Suspended = g.Count(j => j.Status == JobStatus.Suspended),
                    Failed = g.Count(j => j.Status == JobStatus.Failed),
                    Oldest = g.Where(j => j.Status == JobStatus.Pending && j.NotBefore <= now).Min(j => (DateTimeOffset?)j.NotBefore),
                }).ToListAsync(ct))
            .Select(g => new QueueDepth(g.Kind, g.Pending, g.Reserved, g.Suspended, g.Failed, g.Oldest)).OrderBy(q => q.Kind).ToList();
        var outboxPending = db.Outbox.AsNoTracking().Where(o => o.Status == OutboxStatus.Pending || o.Status == OutboxStatus.Reserved);
        var outboxPendingCount = await outboxPending.CountAsync(ct);
        var outboxOldest = outboxPendingCount == 0 ? null : await outboxPending.MinAsync(o => (DateTimeOffset?)o.NotBefore, ct);
        var outboxFailed = await db.Outbox.AsNoTracking().CountAsync(o => o.Status == OutboxStatus.Failed, ct);
        var jobsFailed = await db.Jobs.AsNoTracking().CountAsync(j => j.Status == JobStatus.Failed, ct);
        var deadmanRow = await db.HubComponentHeartbeats.AsNoTracking().SingleOrDefaultAsync(h => h.Component == "deadman", ct);
        var configured = !string.IsNullOrWhiteSpace(configuration["AlertHub:ExternalDeadmanUrl"]);
        var deadman = new DeadmanStatus(configured, deadmanRow?.LastSeen, configured && deadmanRow is not null && now - deadmanRow.LastSeen <= DeadmanStaleAfter);
        return new HubHealth(now, dbOk, components, queues, outboxPendingCount, outboxOldest, outboxFailed, jobsFailed, deadman);
    }

    public async Task<IReadOnlyList<HubFailure>> FailuresAsync(int limit, CancellationToken ct = default)
    {
        var jobs = await db.Jobs.AsNoTracking().Where(j => j.Status == JobStatus.Failed).OrderByDescending(j => j.UpdatedAt).Take(limit)
            .Select(j => new HubFailure(j.JobId, "job", j.Kind, j.UpdatedAt, j.Attempts, j.LastError, j.EpisodeId)).ToListAsync(ct);
        var outbox = await db.Outbox.AsNoTracking().Where(o => o.Status == OutboxStatus.Failed).OrderByDescending(o => o.CreatedAt).Take(limit)
            .Select(o => new HubFailure(o.OutboxId, "outbox", o.Type, o.CreatedAt, o.Attempts, o.LastError, o.EpisodeId)).ToListAsync(ct);
        return jobs.Concat(outbox).OrderByDescending(f => f.At).Take(limit).ToList();
    }

    /// <summary>Re-queues a failed outbox row (jobs go through <see cref="Application.Ops.IJobQueue.RetryFailedAsync"/>).</summary>
    public async Task<bool> RetryOutboxAsync(Guid outboxId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        return await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ops.outbox SET status = 'pending', attempts = 0, not_before = {now}, last_error = NULL WHERE outbox_id = {outboxId} AND status = 'failed'", ct) == 1;
    }
}
