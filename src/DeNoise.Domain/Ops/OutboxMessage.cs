namespace DeNoise.Domain.Ops;

/// <summary>Transactional outbox row (<c>ops.outbox</c>, 05 §3). Written in the same transaction as the transition it announces.</summary>
public sealed class OutboxMessage
{
    public Guid OutboxId { get; init; }
    public Guid? EpisodeId { get; init; }
    public required string Type { get; init; }
    public Guid DestinationId { get; init; }
    public int? PolicyVersion { get; init; }
    public required string Payload { get; init; }
    public string Status { get; set; } = OutboxStatus.Pending;
    public DateTimeOffset NotBefore { get; set; }
    public int Attempts { get; set; }
    public string? ReservedBy { get; set; }
    public DateTimeOffset? ReservedUntil { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}
