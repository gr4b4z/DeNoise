namespace AlertHub.Domain.Alerts;

/// <summary>Delivery idempotency ledger row (<c>alert.applied_event</c>, 05 §2 ★). Insert conflict = duplicate delivery.</summary>
public sealed class AppliedEvent
{
    public Guid IntegrationId { get; init; }
    public required string DeliveryKey { get; init; }
    public Guid EventId { get; init; }
    public DateTimeOffset AppliedAt { get; init; }
    /// <summary>See <see cref="AppliedOutcomes"/>.</summary>
    public required string Outcome { get; init; }
}

public static class AppliedOutcomes
{
    public const string Applied = "applied";
    public const string Late = "late";
    public const string Replayed = "replayed";
    public const string MappingFailed = "mapping_failed";
    /// <summary>Applied to coverage only (heartbeat events never touch episodes).</summary>
    public const string Coverage = "coverage";
}

/// <summary>Counter of retransmissions rejected by the ledger (<c>alert.delivery_duplicate</c>).</summary>
public sealed class DeliveryDuplicate
{
    public Guid IntegrationId { get; init; }
    public required string DeliveryKey { get; init; }
    public int Count { get; set; } = 1;
    public DateTimeOffset FirstSeen { get; init; }
    public DateTimeOffset LastSeen { get; set; }
}

/// <summary>Closed-before-open marker (<c>alert.source_instance_state</c>, 04 §5).</summary>
public sealed class SourceInstanceState
{
    public Guid IntegrationId { get; init; }
    public required string SourceAlertId { get; init; }
    public DateTimeOffset ResolvedAt { get; set; }
}

/// <summary>Stable identity of a monitored condition (<c>alert.identity</c>).</summary>
public sealed class AlertIdentity
{
    public required string Fingerprint { get; init; }
    public Guid IntegrationId { get; init; }
    public required string AccessScope { get; init; }
    public int IdentityVersion { get; init; }
    /// <summary>JSON array of <c>{name, value}</c>.</summary>
    public required string Components { get; init; }
    public DateTimeOffset FirstSeen { get; init; }
    public int EpisodeCount { get; set; }
}

/// <summary>Quarantined event whose interpretation failed (<c>alert.mapping_failure</c>).</summary>
public sealed class MappingFailure
{
    public Guid Id { get; init; }
    public Guid IntegrationId { get; init; }
    public Guid EventId { get; init; }
    public DateTimeOffset RawReceivedAt { get; init; }
    public int? MappingVersion { get; init; }
    public required string Error { get; init; }
    public string? Field { get; init; }
    public bool Quarantined { get; set; } = true;
    public DateTimeOffset? ResolvedAt { get; set; }
    public Guid? ResolvedBy { get; set; }
}
