namespace AlertHub.Domain.Common;

/// <summary>
/// Canonical severity scale (spec §2.1). Closed enum; unmapped source values become <see cref="Unknown"/>.
/// The ordinal is used for comparisons: <c>unknown</c> ranks as <c>high</c> for routing and never below it.
/// </summary>
public enum Severity
{
    Informational = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4,
    Unknown = 5,
}

public static class SeverityExtensions
{
    /// <summary>Ordinal used for "material increase" and routing comparisons. Unknown is treated as high.</summary>
    public static int Rank(this Severity severity) => severity == Severity.Unknown ? (int)Severity.High : (int)severity;

    public static string ToWire(this Severity severity) => severity switch
    {
        Severity.Critical => "critical",
        Severity.High => "high",
        Severity.Medium => "medium",
        Severity.Low => "low",
        Severity.Informational => "informational",
        _ => "unknown",
    };

    public static Severity ParseWire(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "critical" => Severity.Critical,
        "high" => Severity.High,
        "medium" => Severity.Medium,
        "low" => Severity.Low,
        "informational" => Severity.Informational,
        _ => Severity.Unknown,
    };
}
