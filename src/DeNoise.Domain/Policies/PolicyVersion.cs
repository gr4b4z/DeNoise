namespace DeNoise.Domain.Policies;

public static class PolicyKinds
{
    public const string Routing = "routing";
    public const string Escalation = "escalation";
    public const string Lifecycle = "lifecycle";
    public const string Grouping = "grouping";
    public static readonly IReadOnlyList<string> All = [Routing, Escalation, Lifecycle, Grouping];
}

/// <summary>
/// One immutable version of a policy document (<c>cfg.policy</c>; 05 §6 lists one table per kind — this is the same
/// shape with a <see cref="Kind"/> discriminator). ★ at most one active version per (kind, id). DB is truth, YAML is
/// import/export (ADR-6).
/// </summary>
public sealed class PolicyVersion
{
    public Guid PolicyId { get; init; }
    public required string Kind { get; init; }
    public int Version { get; init; }
    public string? Name { get; set; }
    /// <summary>The parsed document as JSON.</summary>
    public required string Body { get; init; }
    public string? SourceYaml { get; init; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public DateTimeOffset? DeactivatedAt { get; set; }
    public string? CreatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    public bool IsActive => ActivatedAt is not null && DeactivatedAt is null;
}
