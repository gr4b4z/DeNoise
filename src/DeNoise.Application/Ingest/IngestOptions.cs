namespace DeNoise.Application.Ingest;

/// <summary>Limits from 06 §2 (initial values; Helm <c>limits.*</c>).</summary>
public sealed class IngestOptions
{
    public const string Section = "Limits";
    /// <summary>Maximum request body, default 256 KiB → 413 above.</summary>
    public int PayloadBytes { get; set; } = 256 * 1024;
    /// <summary>Requests per minute per integration key, default 600 → 429 with Retry-After.</summary>
    public int IngestPerMinute { get; set; } = 600;
    /// <summary>Request headers persisted with the raw event (allow-list; everything else is dropped).</summary>
    public string[] StoredHeaders { get; set; } =
    [
        "Content-Type", "Content-Encoding", "User-Agent", "X-Request-Id", "Traceparent",
        "X-MMS-Signature", "X-MMS-Event", "X-DeNoise-Source-Time",
    ];
}
