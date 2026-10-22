namespace DeNoise.Domain.Ops;

/// <summary>Lifecycle of an <c>ops.job</c> row (04 §11).</summary>
public static class JobStatus
{
    public const string Pending = "pending";
    public const string Reserved = "reserved";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Suspended = "suspended";
    public const string Cancelled = "cancelled";
}

/// <summary>Lifecycle of an <c>ops.outbox</c> row (05 §3).</summary>
public static class OutboxStatus
{
    public const string Pending = "pending";
    public const string Reserved = "reserved";
    public const string Sent = "sent";
    public const string Failed = "failed";
    public const string Coalesced = "coalesced";
    public const string Suppressed = "suppressed";
    public const string Cancelled = "cancelled";
}
