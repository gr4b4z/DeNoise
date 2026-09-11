namespace AlertHub.Domain.Heartbeats;

/// <summary>Heartbeat states (04 §10, spec §13.3.1).</summary>
public static class HeartbeatStates
{
    public const string Unknown = "unknown";
    public const string Healthy = "healthy";
    public const string Late = "late";
    public const string Missed = "missed";
    public const string Paused = "paused";
    public static readonly IReadOnlyList<string> All = [Unknown, Healthy, Late, Missed, Paused];
}

public static class ScheduleKinds
{
    public const string Interval = "interval";
    public const string Cron = "cron";
}

/// <summary>Ping kinds recorded in <c>hb.run</c> (06 §3).</summary>
public static class RunKinds
{
    public const string Success = "success";
    public const string Fail = "fail";
    public const string Start = "start";
    public const string Exit = "exit";
}

/// <summary>A registered heartbeat — a dead-man's switch owned by a team (<c>hb.heartbeat</c>, 05 §5).</summary>
public sealed class Heartbeat
{
    public Guid HeartbeatId { get; init; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string AccessScope { get; set; }
    public Guid OwningTeamId { get; set; }
    public Guid? AssigneeId { get; set; }
    public required string ScheduleKind { get; set; }
    public TimeSpan? Interval { get; set; }
    public string? Cron { get; set; }
    public string? ScheduleTz { get; set; }
    public TimeSpan Grace { get; set; }
    public required string SeverityOnMiss { get; set; }
    public Guid? RoutingPolicyId { get; set; }
    public Guid? BindsToIntegrationId { get; set; }
    public int RecoverySuccessesRequired { get; set; } = 1;
    public bool AutoPauseDuringMaintenance { get; set; } = true;
    public string State { get; set; } = HeartbeatStates.Unknown;
    public DateTimeOffset? ExpectedNext { get; set; }
    public DateTimeOffset? LastPingAt { get; set; }
    public System.Net.IPAddress? LastPingIp { get; set; }
    public TimeSpan? LastRunDuration { get; set; }
    /// <summary>Set by <c>/start</c>; the next terminal ping computes <see cref="LastRunDuration"/> from it.</summary>
    public DateTimeOffset? RunStartedAt { get; set; }
    public int ConsecutiveSuccesses { get; set; }
    public Guid? PausedBy { get; set; }
    public DateTimeOffset? PausedAt { get; set; }
    public string? PauseReason { get; set; }
    /// <summary>State before an automatic maintenance pause, restored at resume; null when paused by a person.</summary>
    public bool PausedByMaintenance { get; set; }
    public required string KeyId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset TokenRotatedAt { get; set; }
    public Guid? MissEpisodeId { get; set; }
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsPaused => State == HeartbeatStates.Paused;

    public void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
    }
}

/// <summary>One ping (<c>hb.run</c>): a ring of the last N per heartbeat, bodies tail-truncated.</summary>
public sealed class HeartbeatRun
{
    public Guid HeartbeatId { get; init; }
    public long Seq { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset FinishedAt { get; init; }
    public required string Kind { get; init; }
    public int? ExitCode { get; init; }
    public string? Body { get; init; }
    public System.Net.IPAddress? SourceIp { get; init; }
}
