namespace DeNoise.Domain.Notifications;

public static class ChannelTypes
{
    public const string Webhook = "webhook";
    public const string SmtpEmail = "smtp_email";
    public static readonly IReadOnlyList<string> All = [Webhook, SmtpEmail];
}

/// <summary>Outbox / notification event types (04 §6, 06 §7).</summary>
public static class NotificationTypes
{
    public const string EpisodeOpened = "episode.opened";
    public const string EpisodeEscalatedSeverity = "episode.escalated_severity";
    public const string EpisodeAckOverdue = "episode.ack_overdue";
    public const string EpisodeFollowUpDue = "episode.follow_up_due";
    public const string EpisodeClosed = "episode.closed";
    public const string EpisodeStaleCritical = "episode.stale_critical";
    public const string EpisodeRoutingFailure = "episode.routing_failure";
    public const string EpisodeAcknowledged = "episode.acknowledged";
    public const string EpisodeSummaryAfterSuppression = "episode.summary_after_suppression";
    public const string HubDeliveryFailure = "hub.delivery_failure";
    public const string CoverageLost = "coverage.lost";
    public const string CoverageRestored = "coverage.restored";
    public const string HeartbeatMissed = "heartbeat.missed";
    public const string HeartbeatRecovered = "heartbeat.recovered";
    public const string DestinationTest = "destination.test";

    public static readonly IReadOnlyList<string> All =
    [
        EpisodeOpened, EpisodeEscalatedSeverity, EpisodeAckOverdue, EpisodeFollowUpDue, EpisodeClosed, EpisodeStaleCritical,
        EpisodeRoutingFailure, EpisodeAcknowledged, EpisodeSummaryAfterSuppression, HubDeliveryFailure, CoverageLost, CoverageRestored,
        HeartbeatMissed, HeartbeatRecovered, DestinationTest,
    ];

    /// <summary>Types that are obsolete once a terminal type for the same episode exists (dispatcher coalescing, 04 §6).</summary>
    public static bool IsSupersededByClosure(string type) => type is EpisodeOpened or EpisodeEscalatedSeverity or EpisodeAckOverdue or EpisodeFollowUpDue or EpisodeStaleCritical;

    /// <summary>Default subscription for a new destination.</summary>
    public static readonly string[] DefaultSubscription = [EpisodeOpened, EpisodeEscalatedSeverity, EpisodeAckOverdue, EpisodeFollowUpDue, EpisodeStaleCritical, EpisodeClosed, EpisodeRoutingFailure, EpisodeSummaryAfterSuppression, HubDeliveryFailure, CoverageLost, CoverageRestored, HeartbeatMissed, HeartbeatRecovered];
}

/// <summary>
/// Delivery target (<c>cfg.destination</c>, ADR-7). Secrets (<c>url</c>, <c>headers</c>, <c>signing_secret</c>) are stored
/// encrypted; the domain object only carries the ciphertext. ★ <see cref="FallbackDestinationId"/> is mandatory and ≠ self.
/// </summary>
public sealed class Destination
{
    public Guid DestinationId { get; init; }
    public Guid? TeamId { get; set; }
    public required string Name { get; set; }
    /// <summary>See <see cref="ChannelTypes"/>.</summary>
    public required string ChannelType { get; init; }
    public string? UrlEnc { get; set; }
    public string Method { get; set; } = "POST";
    /// <summary>Encrypted JSON object of extra request headers.</summary>
    public string? HeadersEnc { get; set; }
    public Guid? BodyTemplateId { get; set; }
    public string? SigningSecretEnc { get; set; }
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
    public string[] EventTypes { get; set; } = NotificationTypes.DefaultSubscription;
    /// <summary>JSON: <c>{ "maxAttempts": 10 }</c>; schedule is fixed (ADR-7).</summary>
    public string? RetryPolicy { get; set; }
    public string[]? EmailTo { get; set; }
    public Guid FallbackDestinationId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? LastFailureAt { get; set; }
    public int ConsecutiveFailures { get; set; }
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }

    public bool Subscribes(string type) => EventTypes.Contains(type, StringComparer.Ordinal) || type == NotificationTypes.DestinationTest;
}

/// <summary>Result of one attempt to contact a destination (<c>ops.delivery_attempt</c>, 05 §3).</summary>
public sealed class DeliveryAttempt
{
    public Guid Id { get; init; }
    public Guid OutboxId { get; init; }
    public DateTimeOffset AttemptedAt { get; init; }
    public required string Channel { get; init; }
    /// <summary>See <see cref="DeliveryOutcomes"/>.</summary>
    public required string Outcome { get; init; }
    public int? HttpStatus { get; init; }
    public int LatencyMs { get; init; }
    public string? Error { get; init; }
    public bool UsedFallback { get; init; }
    /// <summary>First 4 KiB of the receiver's response.</summary>
    public string? ResponseExcerpt { get; init; }
}

public static class DeliveryOutcomes
{
    public const string Success = "success";
    public const string Retryable = "retryable";
    public const string Permanent = "permanent";
    /// <summary>The request was sent but no response arrived; the receiver may have it (spec §17.3).</summary>
    public const string ResponseLost = "response_lost";
}
