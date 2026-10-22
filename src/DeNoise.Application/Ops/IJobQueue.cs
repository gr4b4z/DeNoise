using DeNoise.Domain.Ops;

namespace DeNoise.Application.Ops;

/// <summary>
/// PostgreSQL-backed job queue (ADR-2, 05 §3). Claims are short transactions with
/// <c>FOR UPDATE SKIP LOCKED</c>; every completing write is conditional on the reservation token so a
/// worker that lost its lease cannot commit a stale outcome.
/// </summary>
public interface IJobQueue
{
    /// <summary>Stages a job in the ambient unit of work (committed with the caller's transaction).</summary>
    void Enqueue(Job job);

    /// <summary>Claims up to <paramref name="limit"/> due jobs of the given kinds for <paramref name="workerId"/>.</summary>
    Task<IReadOnlyList<Job>> ClaimAsync(IReadOnlyCollection<string> kinds, string workerId, TimeSpan lease, int limit, CancellationToken ct = default);

    /// <summary>Marks the job done. Returns false when the reservation was lost (expired or reaped) — the outcome must then be treated as not recorded.</summary>
    Task<bool> CompleteAsync(Guid jobId, string workerId, CancellationToken ct = default);

    /// <summary>Records a failure; reschedules with backoff or, when attempts are exhausted, moves the job to the failure queue (<c>failed</c>).</summary>
    Task<JobFailureOutcome> FailAsync(Guid jobId, string workerId, string error, CancellationToken ct = default);

    /// <summary>Extends the lease of a long-running job; false when the reservation was lost.</summary>
    Task<bool> ExtendAsync(Guid jobId, string workerId, TimeSpan lease, CancellationToken ct = default);

    /// <summary>Releases expired reservations back to <c>pending</c> (the reaper, 05 §7).</summary>
    Task<int> ReapExpiredAsync(CancellationToken ct = default);

    /// <summary>Re-queues a job from the failure queue (operator action).</summary>
    Task<bool> RetryFailedAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Stores a handler's result summary on a job it still holds (the reservation is checked); false when lost.</summary>
    Task<bool> RecordResultAsync(Guid jobId, string workerId, string resultJson, CancellationToken ct = default);

    /// <summary>Reads one job (status, attempts, error, result) — replay status endpoint.</summary>
    Task<Job?> GetAsync(Guid jobId, CancellationToken ct = default);
}

public enum JobFailureOutcome
{
    /// <summary>Rescheduled for another attempt.</summary>
    Rescheduled,
    /// <summary>Attempts exhausted; now in the failure queue.</summary>
    MovedToFailureQueue,
    /// <summary>The reservation was no longer ours; nothing recorded.</summary>
    ReservationLost,
}
