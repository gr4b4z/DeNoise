namespace AlertHub.Domain.Ops;

/// <summary>
/// Durable unit of work in <c>ops.job</c>. A record, not an aggregate (04 §1): append/claim/complete only.
/// Reservation semantics: a worker owns the row while <see cref="ReservedBy"/> matches its token and
/// <see cref="ReservedUntil"/> is in the future; completion is a conditional update on both (05 §3 ★).
/// </summary>
public sealed class Job
{
    public Guid JobId { get; init; }
    public required string Kind { get; init; }
    public string Status { get; set; } = JobStatus.Pending;
    public DateTimeOffset NotBefore { get; set; }
    public short Priority { get; init; } = 100;
    public required string Payload { get; init; }
    public Guid? EpisodeId { get; init; }
    public Guid? IntegrationId { get; init; }
    public int? ExpectedVersion { get; init; }
    public DateTimeOffset? ExpectedLastSeen { get; init; }
    public int Attempts { get; set; }
    public int MaxAttempts { get; init; } = 10;
    public string? ReservedBy { get; set; }
    public DateTimeOffset? ReservedUntil { get; set; }
    public string? LastError { get; set; }
    /// <summary>Handler-written summary for jobs that report back (replay, spec §17.5); JSON.</summary>
    public string? Result { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}
