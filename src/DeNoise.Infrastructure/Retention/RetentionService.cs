using System.Diagnostics;
using System.Text.Json;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Retention;
using DeNoise.Domain.Audit;
using DeNoise.Domain.Common;
using DeNoise.Domain.Episodes;
using DeNoise.Domain.Ops;
using DeNoise.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeNoise.Infrastructure.Retention;

/// <summary>
/// <c>retention_raw</c> + <c>retention_rows</c> (05 §7, spec §18.2). Partitions are dropped whole (never today−1 or newer);
/// rows go in batches of <see cref="RetentionOptions.BatchSize"/> per statement so long-running deletes never hold big locks.
/// Open episodes and the events they still need are never touched; the run itself is audited (<c>retention.run</c>) so
/// the hub screen can show when it last happened and what it removed.
/// </summary>
public sealed class RetentionService(DeNoiseDbContext db, RawEventPartitions partitions, IOptions<RetentionOptions> options, IOptions<Application.Heartbeats.HeartbeatOptions> heartbeats, TimeProvider time, ILogger<RetentionService> logger) : IRetentionRunner
{
    public const string RunAction = "retention.run";
    public const string DropAction = "retention.raw_partition_drop";

    public async Task<RetentionReport> RunAsync(CancellationToken ct = default)
    {
        var o = options.Value;
        var now = time.GetUtcNow();
        var watch = Stopwatch.StartNew();
        var correlation = Ids.New(time).ToString("N");
        var rawBoundary = RawBoundary(now, o);

        // retention_raw: whole partitions strictly older than the boundary day; today−1 and newer are never candidates.
        var dropped = new List<string>();
        var boundaryDay = DateOnly.FromDateTime(rawBoundary.UtcDateTime);
        var yesterday = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-1);
        foreach (var day in await partitions.ListAsync(ct))
        {
            if (day >= boundaryDay || day >= yesterday) continue;
            var name = await partitions.DropAsync(day, ct);
            dropped.Add(name);
            db.AuditEntries.Add(Entry(now, DropAction, "raw_event_partition", name, correlation, after: JsonSerializer.Serialize(new { day = day.ToString("yyyy-MM-dd"), rawDays = o.RawDays }, JsonDefaults.Stored)));
            await db.SaveChangesAsync(ct);
        }

        // retention_rows, oldest dependencies first.
        var deleted = new Dictionary<string, int>(StringComparer.Ordinal);
        var normalisedBoundary = now.AddDays(-o.NormalisedClosedDays);
        deleted["normalised_event"] = await DeleteBatchedAsync(
            db.NormalisedEvents.Where(n => n.ReceivedAt < normalisedBoundary && (n.EpisodeId == null || db.Episodes.Any(e => e.EpisodeId == n.EpisodeId && e.HandlingState == HandlingState.Closed))).OrderBy(n => n.ReceivedAt).Select(n => n.EventId),
            ids => db.NormalisedEvents.Where(n => ids.Contains(n.EventId)).ExecuteDeleteAsync(ct), o.BatchSize, ct);

        var episodeBoundary = now.AddMonths(-o.EpisodeClosedMonths);
        deleted["episode"] = await DeleteBatchedAsync(
            db.Episodes.Where(e => e.HandlingState == HandlingState.Closed && e.ClosedAt != null && e.ClosedAt < episodeBoundary).OrderBy(e => e.ClosedAt).Select(e => e.EpisodeId),
            ids => db.Episodes.Where(e => ids.Contains(e.EpisodeId)).ExecuteDeleteAsync(ct), o.BatchSize, ct); // episode_event cascades

        var deliveryBoundary = now.AddDays(-o.DeliveryAttemptDays);
        deleted["delivery_attempt"] = await DeleteBatchedAsync(
            db.DeliveryAttempts.Where(a => a.AttemptedAt < deliveryBoundary).OrderBy(a => a.AttemptedAt).Select(a => a.Id),
            ids => db.DeliveryAttempts.Where(a => ids.Contains(a.Id)).ExecuteDeleteAsync(ct), o.BatchSize, ct);
        deleted["outbox"] = await DeleteBatchedAsync(
            db.Outbox.Where(m => m.CreatedAt < deliveryBoundary && (m.Status == OutboxStatus.Sent || m.Status == OutboxStatus.Coalesced || m.Status == OutboxStatus.Suppressed || m.Status == OutboxStatus.Cancelled)).OrderBy(m => m.CreatedAt).Select(m => m.OutboxId),
            ids => db.Outbox.Where(m => ids.Contains(m.OutboxId)).ExecuteDeleteAsync(ct), o.BatchSize, ct);
        deleted["job"] = await DeleteBatchedAsync(
            db.Jobs.Where(j => j.UpdatedAt < deliveryBoundary && (j.Status == JobStatus.Done || j.Status == JobStatus.Cancelled)).OrderBy(j => j.UpdatedAt).Select(j => j.JobId),
            ids => db.Jobs.Where(j => ids.Contains(j.JobId)).ExecuteDeleteAsync(ct), o.BatchSize, ct);

        var auditBoundary = now.AddMonths(-o.AuditMonths);
        deleted["audit_entry"] = await DeleteBatchedAsync(
            db.AuditEntries.Where(a => a.At < auditBoundary).OrderBy(a => a.At).Select(a => a.Id),
            ids => db.AuditEntries.Where(a => ids.Contains(a.Id)).ExecuteDeleteAsync(ct), o.BatchSize, ct);

        var sessionBoundary = now.AddDays(-o.SessionExpiredDays);
        deleted["session"] = await DeleteBatchedAsync(
            db.Sessions.Where(s => s.ExpiresAt < sessionBoundary || (s.RevokedAt != null && s.RevokedAt < sessionBoundary)).OrderBy(s => s.ExpiresAt).Select(s => s.SessionId),
            ids => db.Sessions.Where(s => ids.Contains(s.SessionId)).ExecuteDeleteAsync(ct), o.BatchSize, ct);

        var loginBoundary = now.AddDays(-o.LoginAttemptDays);
        deleted["login_attempt"] = await DeleteBatchedAsync(
            db.LoginAttempts.Where(l => l.At < loginBoundary).OrderBy(l => l.At).Select(l => l.Id),
            ids => db.LoginAttempts.Where(l => ids.Contains(l.Id)).ExecuteDeleteAsync(ct), o.BatchSize, ct);

        deleted["idempotency"] = await db.IdempotencyRecords.Where(r => r.ExpiresAt < now).ExecuteDeleteAsync(ct);

        // hb.run beyond the ring: keep the newest RunRingSize runs per heartbeat.
        var ring = Math.Max(1, heartbeats.Value.RunRingSize);
        deleted["heartbeat_run"] = await db.Database.ExecuteSqlAsync(
            $"DELETE FROM hb.run r USING (SELECT heartbeat_id, max(seq) - {ring} AS cutoff FROM hb.run GROUP BY heartbeat_id HAVING count(*) > {ring}) c WHERE r.heartbeat_id = c.heartbeat_id AND r.seq <= c.cutoff", ct);

        watch.Stop();
        var report = new RetentionReport(now, rawBoundary, dropped, deleted, watch.ElapsedMilliseconds);
        db.AuditEntries.Add(Entry(now, RunAction, "hub", "retention", correlation, after: JsonSerializer.Serialize(report, JsonDefaults.Stored)));
        await db.SaveChangesAsync(ct);
        logger.LogInformation("retention: dropped {Partitions} partition(s), deleted {Rows} row(s) in {Ms} ms", dropped.Count, report.TotalDeleted, watch.ElapsedMilliseconds);
        return report;
    }

    public async Task<RetentionStatus> StatusAsync(CancellationToken ct = default)
    {
        var o = options.Value;
        var now = time.GetUtcNow();
        var days = await partitions.ListAsync(ct);
        var lastJson = await db.AuditEntries.AsNoTracking().Where(a => a.Action == RunAction).OrderByDescending(a => a.At).Select(a => a.After).FirstOrDefaultAsync(ct);
        RetentionReport? last = null;
        if (lastJson is not null)
        {
            try { last = JsonSerializer.Deserialize<RetentionReport>(lastJson, JsonDefaults.Stored); }
            catch (JsonException) { /* an older shape; the screen shows "unknown" */ }
        }
        return new RetentionStatus(o, RawBoundary(now, o), days.Count == 0 ? null : days[0], days.Count == 0 ? null : days[^1], days.Count, last, RetentionSchedule.NextRun(now, o.RunAtUtcHour));
    }

    /// <summary>Raw events received before this instant may already be gone (start of the oldest retained day).</summary>
    public static DateTimeOffset RawBoundary(DateTimeOffset now, RetentionOptions o)
        => new(now.UtcDateTime.Date.AddDays(-o.RawDays), TimeSpan.Zero);

    private static async Task<int> DeleteBatchedAsync<TKey>(IQueryable<TKey> keys, Func<List<TKey>, Task<int>> delete, int batchSize, CancellationToken ct)
    {
        var total = 0;
        var batch = Math.Max(100, batchSize);
        while (true)
        {
            var ids = await keys.Take(batch).ToListAsync(ct);
            if (ids.Count == 0) return total;
            total += await delete(ids);
            if (ids.Count < batch) return total;
        }
    }

    private AuditEntry Entry(DateTimeOffset now, string action, string targetType, string targetId, string correlation, string? after) => new()
    {
        Id = Ids.New(time),
        At = now,
        ActorType = ActorTypes.System,
        ActorId = "retention",
        ActorDisplay = "DeNoise retention",
        Action = action,
        TargetType = targetType,
        TargetId = targetId,
        After = after,
        CorrelationId = correlation,
    };
}
