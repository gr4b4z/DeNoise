using DeNoise.Application.Notifications;
using DeNoise.Domain.Episodes;
using DeNoise.Domain.Notifications;
using DeNoise.Domain.Ops;
using DeNoise.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DeNoise.Infrastructure.Notifications;

/// <summary>Outbox hot path with the same conditional-update discipline as <see cref="Ops.JobQueue"/>.</summary>
public sealed class EfOutboxQueue(DeNoiseDbContext db, TimeProvider time) : IOutboxQueue
{
    public async Task<IReadOnlyList<OutboxMessage>> ClaimAsync(string workerId, TimeSpan lease, int limit, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var until = now + lease;
        return await db.Outbox.FromSqlInterpolated($"""
            WITH due AS (
                SELECT outbox_id FROM ops.outbox
                WHERE status = 'pending' AND not_before <= {now}
                ORDER BY not_before
                FOR UPDATE SKIP LOCKED
                LIMIT {limit}
            ), claimed AS (
                UPDATE ops.outbox o SET status = 'reserved', reserved_by = {workerId}, reserved_until = {until}, attempts = o.attempts + 1
                FROM due WHERE o.outbox_id = due.outbox_id
                RETURNING o.*
            )
            SELECT * FROM claimed
            """).AsNoTracking().ToListAsync(ct);
    }

    public async Task<bool> MarkSentAsync(Guid outboxId, string workerId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ops.outbox SET status = 'sent', sent_at = {now}, reserved_by = NULL, reserved_until = NULL, last_error = NULL
            WHERE outbox_id = {outboxId} AND status = 'reserved' AND reserved_by = {workerId} AND reserved_until > {now}
            """, ct) == 1;
    }

    public async Task<bool> RescheduleAsync(Guid outboxId, string workerId, DateTimeOffset notBefore, string error, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var truncated = error.Length > 2000 ? error[..2000] : error;
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ops.outbox SET status = 'pending', not_before = {notBefore}, reserved_by = NULL, reserved_until = NULL, last_error = {truncated}
            WHERE outbox_id = {outboxId} AND status = 'reserved' AND reserved_by = {workerId} AND reserved_until > {now}
            """, ct) == 1;
    }

    public async Task<bool> MarkFailedAsync(Guid outboxId, string workerId, string error, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var truncated = error.Length > 2000 ? error[..2000] : error;
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ops.outbox SET status = 'failed', reserved_by = NULL, reserved_until = NULL, last_error = {truncated}
            WHERE outbox_id = {outboxId} AND status = 'reserved' AND reserved_by = {workerId} AND reserved_until > {now}
            """, ct) == 1;
    }

    public async Task<bool> MarkCoalescedAsync(Guid outboxId, string workerId, string reason, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ops.outbox SET status = 'coalesced', reserved_by = NULL, reserved_until = NULL, last_error = {reason}
            WHERE outbox_id = {outboxId} AND status = 'reserved' AND reserved_by = {workerId} AND reserved_until > {now}
            """, ct) == 1;
    }

    public Task<bool> HasLaterTerminalAsync(Guid episodeId, DateTimeOffset createdAfter, CancellationToken ct = default)
        => db.Outbox.AsNoTracking().AnyAsync(o => o.EpisodeId == episodeId && o.Type == NotificationTypes.EpisodeClosed && o.CreatedAt > createdAfter, ct);

    public async Task EnqueueAsync(OutboxMessage message, CancellationToken ct = default)
    {
        db.Outbox.Add(message);
        await db.SaveChangesAsync(ct);
        db.Entry(message).State = EntityState.Detached;
    }

    public async Task RecordAttemptAsync(DeliveryAttempt attempt, CancellationToken ct = default)
    {
        db.DeliveryAttempts.Add(attempt);
        await db.SaveChangesAsync(ct);
        db.Entry(attempt).State = EntityState.Detached;
    }

    public Task UpdateDestinationHealthAsync(Guid destinationId, bool success, DateTimeOffset at, CancellationToken ct = default)
        => success
            ? db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cfg.destination SET last_success_at = {at}, consecutive_failures = 0 WHERE destination_id = {destinationId}", ct)
            : db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cfg.destination SET last_failure_at = {at}, consecutive_failures = consecutive_failures + 1 WHERE destination_id = {destinationId}", ct);

    public Task<int> ReleaseSuppressedAsync(Guid episodeId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        return db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ops.outbox SET status = 'pending', not_before = {now} WHERE episode_id = {episodeId} AND status = 'suppressed'", ct);
    }
}

public sealed class EfEpisodeReader(DeNoiseDbContext db) : IEpisodeReader
{
    public Task<Episode?> GetAsync(Guid episodeId, CancellationToken ct = default)
        => db.Episodes.AsNoTracking().SingleOrDefaultAsync(e => e.EpisodeId == episodeId, ct);
}
