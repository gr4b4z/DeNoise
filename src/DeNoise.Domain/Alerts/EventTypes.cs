namespace DeNoise.Domain.Alerts;

/// <summary>Canonical <c>event_type</c> values (spec §9).</summary>
public static class EventTypes
{
    public const string Firing = "firing";
    public const string Update = "update";
    public const string Resolved = "resolved";
    public const string Acknowledged = "acknowledged";
    public const string Cancelled = "cancelled";
    public const string Informational = "informational";
    public const string Heartbeat = "heartbeat";

    public static readonly IReadOnlyList<string> All = [Firing, Update, Resolved, Acknowledged, Cancelled, Informational, Heartbeat];

    public static bool IsKnown(string? value) => value is not null && All.Contains(value);
}

/// <summary>Canonical target field names a mapping may set (spec §9, 07 §1).</summary>
public static class CanonicalFields
{
    public const string EventType = "event_type";
    public const string SourceAlertId = "source_alert_id";
    public const string SourceEventId = "source_event_id";
    public const string SourceVersion = "source_version";
    public const string OccurredAt = "occurred_at";
    public const string Severity = "severity";
    public const string SourceSeverity = "source_severity";
    public const string ResourceId = "resource_id";
    public const string ResourceName = "resource_name";
    public const string RuleId = "rule_id";
    public const string RuleName = "rule_name";
    public const string Environment = "environment";
    public const string Service = "service";
    public const string Summary = "summary";
    public const string SourceUrl = "source_url";
    public const string RunbookUrl = "runbook_url";
    public const string Dimensions = "dimensions";
    public const string Labels = "labels";

    public static readonly IReadOnlyList<string> All =
    [
        EventType, SourceAlertId, SourceEventId, SourceVersion, OccurredAt, Severity, SourceSeverity, ResourceId, ResourceName,
        RuleId, RuleName, Environment, Service, Summary, SourceUrl, RunbookUrl, Dimensions, Labels,
    ];

    /// <summary>Fields whose value is an object (string → string) rather than a scalar.</summary>
    public static readonly IReadOnlyList<string> ObjectFields = [Dimensions, Labels];
}
