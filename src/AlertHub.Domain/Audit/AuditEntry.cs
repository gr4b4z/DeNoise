namespace AlertHub.Domain.Audit;

/// <summary>Append-only audit row (<c>audit.entry</c>, 05 §4), written in the same transaction as the change it records.</summary>
public sealed class AuditEntry
{
    public Guid Id { get; init; }
    public DateTimeOffset At { get; init; }
    /// <summary><c>user</c> | <c>system</c> | <c>integration</c>.</summary>
    public required string ActorType { get; init; }
    public required string ActorId { get; init; }
    public string? ActorDisplay { get; init; }
    /// <summary>Dotted verb, e.g. <c>integration.create</c>, <c>episode.ack</c>.</summary>
    public required string Action { get; init; }
    public required string TargetType { get; init; }
    public required string TargetId { get; init; }
    public string? AccessScope { get; init; }
    public string? Before { get; init; }
    public string? After { get; init; }
    public string? Reason { get; init; }
    public required string CorrelationId { get; init; }
    public System.Net.IPAddress? RequestIp { get; init; }
}

public static class ActorTypes
{
    public const string User = "user";
    public const string System = "system";
    public const string Integration = "integration";
}
