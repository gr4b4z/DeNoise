namespace AlertHub.Domain.Episodes;

/// <summary>What Alert Hub believes about the monitored system (spec §11.1).</summary>
public static class ConditionState
{
    public const string Firing = "firing";
    public const string Resolved = "resolved";
    public const string Unknown = "unknown";
    public const string NotApplicable = "not_applicable";
}

/// <summary>What people have done about it (spec §11.1).</summary>
public static class HandlingState
{
    public const string New = "new";
    public const string Acknowledged = "acknowledged";
    public const string Closed = "closed";
}

/// <summary>Closure reasons (spec §11.2).</summary>
public static class ClosureReason
{
    public const string SourceResolved = "source_resolved";
    public const string VerifiedResolved = "verified_resolved";
    public const string InactivityTimeout = "inactivity_timeout";
    public const string ExpiredUnverified = "expired_unverified";
    public const string ManualClose = "manual_close";
    public const string InformationalCompleted = "informational_completed";
    public const string SourceCancelled = "source_cancelled";
}

/// <summary>Resolution evidence, recorded separately from the reason (spec §11.2, §12.2).</summary>
public static class Evidence
{
    public const string Source = "source";
    public const string ApiVerification = "api_verification";
    public const string HeartbeatAndInactivity = "heartbeat_and_inactivity";
    public const string InactivityUnverified = "inactivity_unverified";
    public const string Human = "human";
    public const string None = "none";
}

/// <summary>Timeline entry kinds (<c>alert.episode_event.kind</c>, 05 §2).</summary>
public static class EpisodeEventKind
{
    public const string SourceEvent = "source_event";
    public const string LateEvent = "late_event";
    public const string Replayed = "replayed";
    public const string Ack = "ack";
    public const string Assign = "assign";
    public const string Takeover = "takeover";
    public const string Note = "note";
    public const string Close = "close";
    public const string Restore = "restore";
    public const string AutoResolve = "auto_resolve";
    public const string Suspend = "suspend";
    public const string Resume = "resume";
    public const string Notify = "notify";
    public const string Escalate = "escalate";
    public const string Suppress = "suppress";
    public const string Postpone = "postpone";
    public const string StaleReview = "stale_review";
    public const string Expire = "expire";
    public const string Coverage = "coverage";
}
