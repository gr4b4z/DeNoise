namespace AlertHub.Domain.Policies;

public static class SuppressionKinds
{
    public const string Silence = "silence";
    public const string Maintenance = "maintenance";
    public static readonly IReadOnlyList<string> All = [Silence, Maintenance];
}

/// <summary>
/// A silence or maintenance window (<c>cfg.suppression</c>, 04 §7.5, spec §16.3): time-bounded, scoped by a predicate,
/// with a mandatory reason and an explicit time zone. Suppression blocks delivery only — never ingestion, ownership or history.
/// </summary>
public sealed class Suppression
{
    public Guid SuppressionId { get; init; }
    /// <summary>See <see cref="SuppressionKinds"/>.</summary>
    public required string Kind { get; init; }
    public string? Name { get; set; }
    /// <summary>Predicate JSON (07 §5) over the episode / event references.</summary>
    public required string Scope { get; init; }
    public DateTimeOffset StartsAt { get; init; }
    public DateTimeOffset EndsAt { get; set; }
    /// <summary>IANA zone the window was declared in; instants are stored in UTC, the zone is for display and wall-clock edits.</summary>
    public required string TimeZone { get; init; }
    public required string Reason { get; init; }
    public bool AutoPauseHeartbeats { get; init; } = true;
    public string? CreatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelledBy { get; set; }
    /// <summary>Set when the post-suppression summary was staged (idempotent end handling).</summary>
    public DateTimeOffset? SummarySentAt { get; set; }
    /// <summary>Set when existing open episodes were evaluated at the start (idempotent start handling).</summary>
    public DateTimeOffset? StartedAt { get; set; }

    public bool IsActiveAt(DateTimeOffset now) => CancelledAt is null && StartsAt <= now && now < EndsAt;
    public bool HasEnded(DateTimeOffset now) => CancelledAt is not null || EndsAt <= now;
}
