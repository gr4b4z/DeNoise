namespace AlertHub.Domain.Ops;

/// <summary>Timer catalogue, 04 §11. Values are the <c>ops.job.kind</c> column.</summary>
public static class JobKinds
{
    public const string Normalise = "normalise";
    public const string AutoResolve = "auto_resolve";
    public const string VerifyState = "verify_state";
    public const string AckDeadline = "ack_deadline";
    public const string FollowUp = "follow_up";
    public const string EscalationStep = "escalation_step";
    public const string InformationalExpiry = "informational_expiry";
    public const string StaleReview = "stale_review";
    public const string AdminExpiry = "admin_expiry";
    public const string GroupWindowClose = "group_window_close";
    public const string SuppressionEnd = "suppression_end";
    public const string CoverageCheck = "coverage_check";
    public const string HeartbeatCheck = "heartbeat_check";
    public const string ApiProbe = "api_probe";
    public const string RetentionRaw = "retention_raw";
    public const string RetentionRows = "retention_rows";
    public const string PartitionCreate = "partition_create";
    public const string DeadmanPing = "deadman_ping";
    public const string Replay = "replay";

    public static readonly IReadOnlyList<string> All =
    [
        Normalise, AutoResolve, VerifyState, AckDeadline, FollowUp, EscalationStep, InformationalExpiry,
        StaleReview, AdminExpiry, GroupWindowClose, SuppressionEnd, CoverageCheck, HeartbeatCheck, ApiProbe,
        RetentionRaw, RetentionRows, PartitionCreate, DeadmanPing, Replay,
    ];

    /// <summary>Kinds executed by the processing role.</summary>
    public static readonly IReadOnlyList<string> Processing = [Normalise, Replay];

    /// <summary>Kinds executed by the scheduler role.</summary>
    public static readonly IReadOnlyList<string> Scheduler = All.Except(Processing).ToArray();
}
