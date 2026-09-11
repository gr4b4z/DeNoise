namespace AlertHub.Domain.Teams;

/// <summary>Owning team (<c>cfg.team</c>, 05 §6). Exactly one team is the fallback triage team (spec §7.2).</summary>
public sealed class Team
{
    public Guid TeamId { get; init; }
    public required string Name { get; set; }
    public string[] AccessScopes { get; set; } = [];
    public Guid? FallbackTeamId { get; set; }
    /// <summary>JSON: <c>{ "tz": "Europe/Warsaw", "days": [1..5], "from": "08:00", "to": "18:00" }</c>; used only when an escalation policy is business-hours-only.</summary>
    public string? CoverageHours { get; set; }
    public Guid? DefaultEscalationPolicyId { get; set; }
    public string[] EntraGroupIds { get; set; } = [];
    /// <summary>The team that receives alerts matching no routing rule (spec §7.2). At most one.</summary>
    public bool IsTriage { get; set; }
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Product access scope (<c>cfg.scope</c>); seeded from configuration (C6).</summary>
public sealed class AccessScope
{
    public required string Scope { get; init; }
    public string? Product { get; set; }
    public string[] EntraGroupIds { get; set; } = [];
}
