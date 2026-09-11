using AlertHub.Domain.Notifications;
using AlertHub.Domain.Ops;

namespace AlertHub.Application.Notifications;

/// <summary>Decrypted view of a destination for one send; never persisted or logged.</summary>
public sealed record ResolvedDestination(Destination Destination, string? Url, IReadOnlyDictionary<string, string> Headers, string? SigningSecret);

/// <summary>Result of one channel send. The dispatcher records it as a <see cref="DeliveryAttempt"/> and decides the outbox transition.</summary>
public sealed record ChannelResult(string Outcome, int? HttpStatus = null, string? Error = null, string? ResponseExcerpt = null, TimeSpan? RetryAfter = null)
{
    public static ChannelResult Success(int? status = null, string? excerpt = null) => new(DeliveryOutcomes.Success, status, null, excerpt);
    public static ChannelResult Retryable(string error, int? status = null, string? excerpt = null, TimeSpan? retryAfter = null) => new(DeliveryOutcomes.Retryable, status, error, excerpt, retryAfter);
    public static ChannelResult Permanent(string error, int? status = null, string? excerpt = null) => new(DeliveryOutcomes.Permanent, status, error, excerpt);
    public static ChannelResult ResponseLost(string error) => new(DeliveryOutcomes.ResponseLost, null, error);
}

/// <summary>Exactly two implementations in v1: webhook and SMTP e-mail (ADR-7).</summary>
public interface INotificationChannel
{
    string ChannelType { get; }
    Task<ChannelResult> SendAsync(ResolvedDestination destination, OutboxMessage message, string body, CancellationToken ct);
}

/// <summary>Retry schedule from ADR-7: 30 s → 1 m → 5 m → 15 m → 1 h, then hourly, max 10 attempts; <c>Retry-After</c> wins when present.</summary>
public static class DeliveryBackoff
{
    private static readonly TimeSpan[] Schedule = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1)];

    public static TimeSpan For(int attemptsSoFar, TimeSpan? retryAfter = null)
    {
        var scheduled = Schedule[Math.Clamp(attemptsSoFar - 1, 0, Schedule.Length - 1)];
        if (retryAfter is { } ra && ra > TimeSpan.Zero) return ra > TimeSpan.FromHours(6) ? TimeSpan.FromHours(6) : ra;
        return scheduled;
    }
}
