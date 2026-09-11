using System.Globalization;
using AlertHub.Domain.Alerts;

namespace AlertHub.Domain.Episodes;

/// <summary>Ordering rules per source instance (04 §5).</summary>
public static class Ordering
{
    public static readonly TimeSpan DefaultClockSkewTolerance = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Compares producer versions: numerically when both parse as integers, otherwise ordinally.
    /// Returns &gt; 0 when <paramref name="candidate"/> is newer than <paramref name="applied"/>.
    /// </summary>
    public static int CompareVersions(string candidate, string? applied)
    {
        if (applied is null) return 1;
        if (long.TryParse(candidate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c)
            && long.TryParse(applied, NumberStyles.Integer, CultureInfo.InvariantCulture, out var a))
        {
            return c.CompareTo(a);
        }
        return string.CompareOrdinal(candidate, applied);
    }

    /// <summary>Decides whether an event for an open episode is effective or late.</summary>
    public static bool IsEffective(Episode episode, NormalisedEvent evt, TimeSpan? skew = null)
    {
        if (!string.IsNullOrEmpty(evt.SourceVersion))
        {
            return CompareVersions(evt.SourceVersion, episode.LastAppliedVersion) > 0;
        }
        var tolerance = skew ?? DefaultClockSkewTolerance;
        var at = evt.EffectiveAt;
        if (evt.EventType == EventTypes.Resolved && evt.OccurredAt is not null && evt.OccurredAt < episode.LastSeen)
        {
            // A recovery older than the newest firing in this episode cannot close it.
            return false;
        }
        return at >= episode.LastSeen - tolerance;
    }
}
