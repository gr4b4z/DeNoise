namespace AlertHub.Domain.Episodes;

/// <summary>
/// A bounded-window group of episodes sharing a grouping key (<c>alert.alert_group</c>, 04 §7.4, spec §16.3). Members keep
/// their own lifecycles; the group only carries the max active severity and never spans access scopes.
/// </summary>
public sealed class AlertGroup
{
    public Guid GroupId { get; init; }
    public required string AccessScope { get; init; }
    public Guid RuleId { get; init; }
    /// <summary>Canonical JSON object of the key fields, e.g. <c>{"environment":"production","service":"orders"}</c>.</summary>
    public required string KeyValues { get; init; }
    public DateTimeOffset OpenedAt { get; init; }
    public DateTimeOffset WindowEndsAt { get; init; }
    public string Severity { get; set; } = "unknown";
    public int MemberCount { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }

    public bool IsOpen => ClosedAt is null;
}
