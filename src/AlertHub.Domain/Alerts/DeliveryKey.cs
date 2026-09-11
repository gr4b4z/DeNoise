using System.Globalization;
using System.Security.Cryptography;

namespace AlertHub.Domain.Alerts;

/// <summary>Delivery idempotency key per integration (04 §3). At most 512 characters.</summary>
public static class DeliveryKey
{
    public const int MaxLength = 512;
    public const string ConfidenceExact = "exact";
    public const string ConfidenceBody = "body";

    public sealed record Result(string Key, string Confidence);

    /// <param name="occurredAtReliable">True when the mapping produced <c>occurred_at</c> from the payload (not from receipt time).</param>
    public static Result Compute(
        string? sourceEventId, string? sourceAlertId, string? sourceVersion, string? eventType, DateTimeOffset? occurredAt, bool occurredAtReliable,
        ReadOnlySpan<byte> body, IReadOnlyCollection<string>? ignorePaths)
    {
        if (!string.IsNullOrEmpty(sourceEventId))
        {
            return new Result(Truncate("evt:" + sourceEventId), ConfidenceExact);
        }
        if (!string.IsNullOrEmpty(sourceAlertId) && !string.IsNullOrEmpty(sourceVersion))
        {
            return new Result(Truncate($"ver:{sourceAlertId}:{sourceVersion}"), ConfidenceExact);
        }
        if (!string.IsNullOrEmpty(sourceAlertId) && !string.IsNullOrEmpty(eventType) && occurredAt is not null && occurredAtReliable)
        {
            var ts = occurredAt.Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            return new Result(Truncate($"ts:{sourceAlertId}:{eventType}:{ts}"), ConfidenceExact);
        }

        byte[] canonical;
        try
        {
            canonical = CanonicalJson.Canonicalise(body, ignorePaths);
        }
        catch (System.Text.Json.JsonException)
        {
            canonical = body.ToArray(); // not JSON: hash the raw bytes
        }
        return new Result("body:" + Convert.ToHexStringLower(SHA256.HashData(canonical)), ConfidenceBody);
    }

    private static string Truncate(string key) => key.Length <= MaxLength ? key : key[..MaxLength];
}
