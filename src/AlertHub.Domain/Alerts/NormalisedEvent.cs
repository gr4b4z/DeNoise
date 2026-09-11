using AlertHub.Domain.Common;

namespace AlertHub.Domain.Alerts;

/// <summary>
/// Interpreted source fact (spec §9; <c>alert.normalised_event</c>, 05 §2). Everything the episode logic needs
/// about one accepted event; ownership, acknowledgement and closure never live here.
/// </summary>
public sealed class NormalisedEvent
{
    public Guid EventId { get; init; }
    public Guid IntegrationId { get; init; }
    public DateTimeOffset RawReceivedAt { get; init; }
    public int MappingVersion { get; init; }
    public required string EventType { get; init; }
    public string? SourceAlertId { get; init; }
    public string? SourceEventId { get; init; }
    public string? SourceVersion { get; init; }
    public DateTimeOffset? OccurredAt { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }
    public Severity Severity { get; init; }
    public string? SourceSeverity { get; init; }
    public string? ResourceId { get; init; }
    public string? ResourceName { get; init; }
    public string? RuleId { get; init; }
    public string? RuleName { get; init; }
    public string? Environment { get; init; }
    public string? Service { get; init; }
    public string? Summary { get; init; }
    public string? SourceUrl { get; init; }
    public string? RunbookUrl { get; init; }
    /// <summary>Identity-relevant dimensions, JSON object of strings.</summary>
    public IReadOnlyDictionary<string, string>? Dimensions { get; init; }
    /// <summary>Additional metadata, JSON object of strings.</summary>
    public IReadOnlyDictionary<string, string>? Labels { get; init; }
    /// <summary>Lowercase hex SHA-256; null for heartbeat and identity-less informational events.</summary>
    public string? Fingerprint { get; init; }
    /// <summary>The components that produced <see cref="Fingerprint"/>, for explainability.</summary>
    public IReadOnlyList<IdentityComponent>? IdentityComponents { get; init; }
    public required string DeliveryKey { get; init; }
    /// <summary><c>exact</c> when the key came from producer ids, <c>body</c> when it fell back to a body hash (04 §3).</summary>
    public required string IdentityConfidence { get; init; }
    /// <summary>Lifecycle profile hint from the mapping (spec §12.1); policies may override per rule.</summary>
    public string? LifecycleProfileHint { get; init; }
    /// <summary>Set once the event has been applied to an episode.</summary>
    public Guid? EpisodeId { get; set; }

    /// <summary>The effective point in time for ordering and <c>last_seen</c>: source time where reliable, else receipt.</summary>
    public DateTimeOffset EffectiveAt => OccurredAt ?? ReceivedAt;

    public bool IsActionableType => EventType is EventTypes.Firing or EventTypes.Update or EventTypes.Resolved or EventTypes.Cancelled;
}

/// <summary>One named identity component and its canonicalised value (04 §4).</summary>
public sealed record IdentityComponent(string Name, string Value);
