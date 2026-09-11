namespace AlertHub.Domain.Alerts;

/// <summary>
/// Immutable received body plus permitted request metadata (<c>alert.raw_event</c>, 05 §2).
/// The table is range-partitioned by <see cref="ReceivedAt"/> from the first migration; the primary key is
/// (received_at, event_id) because PostgreSQL requires the partition key inside every unique constraint.
/// </summary>
public sealed class RawEvent
{
    public Guid EventId { get; init; }
    public Guid IntegrationId { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }
    public string? ContentType { get; init; }
    public required byte[] Body { get; init; }
    /// <summary>Allow-listed request headers only, serialised as a JSON object.</summary>
    public string? Headers { get; init; }
    public System.Net.IPAddress? SourceIp { get; init; }
    public int SizeBytes { get; init; }
}
