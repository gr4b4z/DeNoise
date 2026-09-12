using DeNoise.Application.Ops;
using DeNoise.Domain.Ops;
using DeNoise.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DeNoise.Infrastructure.Ops;

/// <summary>
/// Hand-written SQL for the queue hot path (ADR-4); every finishing statement is conditional on
/// <c>(job_id, reserved_by, reserved_until)</c> so a stale worker can never record an outcome (05 §3 ★).
/// </summary>
public sealed class JobQueue(DeNoiseDbContext db, TimeProvider time) : IJobQueue
{
    public void Enqueue(Job job) => db.Jobs.Add(job);

    public async Task<IReadOnlyList<Job>> ClaimAsync(IReadOnlyCollection<string> kinds, string workerId, TimeSpan lease, int limit, CancellationToken ct = default)
    {
        if (kinds.Count == 0 || limit <= 0) return [];
        var now = time.GetUtcNow();
        var until = now + lease;
        var kindArray = kinds.ToArray();
        var claimed = await db.Jobs.FromSqlInterpolated($"""
            WITH due AS (
                SELECT job_id FROM ops.job
                WHERE status = 'pending' AND kind = ANY({kindArray}) AND not_before <= {now}
                ORDER BY priority, not_before
                FOR UPDATE SKIP LOCKED
                LIMIT {limit}
            ), claimed AS (
                UPDATE ops.job j
                SET status = 'reserved', reserved_by = {workerId}, reserved_until = {until},
                    attempts = j.attempts + 1, updated_at = {now}
                FROM due WHERE j.job_id = due.job_id
                RETURNING j.*
            )
            SELECT * FROM claimed
            """).AsNoTracking().ToListAsync(ct);
        return claimed;
    }

    public async Task<bool> CompleteAsync(Guid jobId, string workerId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ops.job SET status = 'done', reserved_by = NULL, reserved_until = NULL, updated_at = {now}
            WHERE job_id = {jobId} AND status = 'reserved' AND reserved_by = {workerId} AND reserved_until > {now}
            """, ct);
        return rows == 1;
    }

    public async Task<bool> RecordResultAsync(Guid jobId, string workerId, string resultJson, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ops.job SET result = {resultJson}::jsonb, updated_at = {now}
            WHERE job_id = {jobId} AND status = 'reserved' AND reserved_by = {workerId} AND reserved_until > {now}
            """, ct);
        return rows == 1;
    }

    public Task<Job?> GetAsync(Guid jobId, CancellationToken ct = default) => db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.JobId == jobId, ct);

    public async Task<JobFailureOutcome> FailAsync(Guid jobId, string workerId, string error, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var truncated = error.Length > 4000 ? error[..4000] : error;
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.JobId == jobId, ct);
        if (job is null || job.Status != JobStatus.Reserved || job.ReservedBy != workerId || job.ReservedUntil <= now)
        {
            return JobFailureOutcome.ReservationLost;
        }

        var exhausted = job.Attempts >= job.MaxAttempts;
        var retryAt = now + Backoff.For(job.Attempts);
        var rows = exhausted
            ? await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE ops.job SET status = 'failed', reserved_by = NULL, reserved_until = NULL, last_error = {truncated}, updated_at = {now}
                WHERE job_id = {jobId} AND status = 'reserved' AND reserved_by = {workerId} AND reserved_until > {now}
                """, ct)
            : await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE ops.job SET status = 'pending', reserved_by = NULL, reserved_until = NULL, not_before = {retryAt}, last_error = {truncated}, updated_at = {now}
                WHERE job_id = {jobId} AND status = 'reserved' AND reserved_by = {workerId} AND reserved_until > {now}
                """, ct);
        if (rows != 1) return JobFailureOutcome.ReservationLost;
        return exhausted ? JobFailureOutcome.MovedToFailureQueue : JobFailureOutcome.Rescheduled;
    }

    public async Task<bool> ExtendAsync(Guid jobId, string workerId, TimeSpan lease, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var until = now + lease;
        var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ops.job SET reserved_until = {until}, updated_at = {now}
            WHERE job_id = {jobId} AND status = 'reserved' AND reserved_by = {workerId} AND reserved_until > {now}
            """, ct);
        return rows == 1;
    }

    public Task<int> ReapExpiredAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ops.job SET status = 'pending', reserved_by = NULL, reserved_until = NULL, updated_at = {now},
                last_error = 'reservation by ' || reserved_by || ' expired at ' || reserved_until::text
            WHERE status = 'reserved' AND reserved_until < {now}
            """, ct);
    }

    public async Task<bool> RetryFailedAsync(Guid jobId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ops.job SET status = 'pending', attempts = 0, not_before = {now}, updated_at = {now}
            WHERE job_id = {jobId} AND status = 'failed'
            """, ct);
        return rows == 1;
    }
}
