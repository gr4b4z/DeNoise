namespace DeNoise.Domain.Episodes;

/// <summary>
/// A bounded-window group of episodes sharing a grouping key (<c>alert.alert_group</c>, 04 §7.4, spec §16.3). Members keep
/// their own lifecycles; the group only carries the max active severity and never spans access scopes. The window is bounded,
/// not fixed: <see cref="WindowEndsAt"/> may move out with new members but never past <see cref="WindowHardEndAt"/>.
/// </summary>
public sealed class AlertGroup
{
    public Guid GroupId { get; init; }
    public required string AccessScope { get; init; }
    public Guid RuleId { get; init; }
    /// <summary>Canonical JSON object of the key fields, e.g. <c>{"environment":"production","service":"orders"}</c>.</summary>
    public required string KeyValues { get; init; }
    public DateTimeOffset OpenedAt { get; init; }
    /// <summary>Current end of the window; moves out by the rule's extension on each member, up to <see cref="WindowHardEndAt"/>.</summary>
    public DateTimeOffset WindowEndsAt { get; set; }
    /// <summary>First member + the rule's maximum: the extension can never pass it (spec §16.3 "the maximum is mandatory").</summary>
    public DateTimeOffset WindowHardEndAt { get; set; }
    /// <summary>When the latest member joined; drives the next extension.</summary>
    public DateTimeOffset LastMemberAt { get; set; }
    /// <summary>How many times the window was extended — the UI says "window extended 4×".</summary>
    public int ExtensionCount { get; set; }
    /// <summary>Members taken in retroactively (spec §16.3 "Retroactive adoption").</summary>
    public int AdoptedCount { get; set; }
    /// <summary>Next digest due; null when the rule has no digest or the group emitted its last one.</summary>
    public DateTimeOffset? DigestNextAt { get; set; }
    /// <summary>When the last digest was staged; members that joined after it go into the next one.</summary>
    public DateTimeOffset? DigestLastAt { get; set; }
    /// <summary>Set when this group started because its predecessor for the same key reached its maximum or its grace: a storm reads as a chain.</summary>
    public Guid? ContinuedFromGroupId { get; init; }
    public string Severity { get; set; } = "unknown";
    public int MemberCount { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }

    public bool IsOpen => ClosedAt is null;

    /// <summary>A member arriving at <paramref name="at"/> still joins while the window, plus the late-join grace, has not passed.</summary>
    public bool Accepts(DateTimeOffset at, TimeSpan lateJoin) => IsOpen && at <= WindowEndsAt + lateJoin;

    /// <summary>Pushes the end out by <paramref name="extendBy"/>, capped at the hard end. Returns true when the end moved.</summary>
    public bool Extend(TimeSpan? extendBy)
    {
        if (extendBy is not { } by || by <= TimeSpan.Zero) return false;
        var proposed = WindowEndsAt + by;
        if (proposed > WindowHardEndAt) proposed = WindowHardEndAt;
        if (proposed <= WindowEndsAt) return false;
        WindowEndsAt = proposed;
        ExtensionCount++;
        return true;
    }
}
