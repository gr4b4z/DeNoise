namespace AlertHub.Application.Retention;

/// <summary>Spec §18.2 / 05 §7 retention values; bound from <c>Retention:*</c> (Helm <c>retention.*</c>).</summary>
public sealed class RetentionOptions
{
    public const string Section = "Retention";

    /// <summary>Raw payload partitions older than this are dropped (<c>retention_raw</c>).</summary>
    public int RawDays { get; set; } = 30;
    /// <summary>Normalised events older than this are deleted when their episode is closed (or they never attached to one).</summary>
    public int NormalisedClosedDays { get; set; } = 90;
    /// <summary>Closed episodes (with their timeline) older than this are deleted. Open episodes are never touched.</summary>
    public int EpisodeClosedMonths { get; set; } = 12;
    /// <summary>Delivery attempts, terminal outbox rows and done jobs older than this are deleted.</summary>
    public int DeliveryAttemptDays { get; set; } = 30;
    public int AuditMonths { get; set; } = 12;
    public int SessionExpiredDays { get; set; } = 7;
    public int LoginAttemptDays { get; set; } = 30;
    /// <summary>Rows deleted per transaction (05 §7: 5,000).</summary>
    public int BatchSize { get; set; } = 5000;
    /// <summary>Daily run time, UTC hour (05 §7: 02:00).</summary>
    public int RunAtUtcHour { get; set; } = 2;
}

/// <summary>One retention run: what was dropped and deleted. Recorded as the <c>retention.run</c> audit entry.</summary>
public sealed record RetentionReport(DateTimeOffset At, DateTimeOffset RawBoundary, IReadOnlyList<string> DroppedPartitions, IReadOnlyDictionary<string, int> Deleted, long DurationMs)
{
    public int TotalDeleted => Deleted.Values.Sum();
}

/// <summary>What the hub health screen shows: the configured values, the raw boundary, the partition range and the last run.</summary>
public sealed record RetentionStatus(RetentionOptions Options, DateTimeOffset RawBoundary, DateOnly? OldestPartition, DateOnly? NewestPartition, int PartitionCount, RetentionReport? LastRun, DateTimeOffset NextRunAt);

public interface IRetentionRunner
{
    Task<RetentionReport> RunAsync(CancellationToken ct = default);
    Task<RetentionStatus> StatusAsync(CancellationToken ct = default);
}

public static class RetentionSchedule
{
    /// <summary>The next daily run at <paramref name="utcHour"/>:00 UTC strictly after <paramref name="now"/>.</summary>
    public static DateTimeOffset NextRun(DateTimeOffset now, int utcHour)
    {
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddHours(Math.Clamp(utcHour, 0, 23));
        return today > now ? today : today.AddDays(1);
    }
}
