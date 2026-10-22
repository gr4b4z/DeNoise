using Cronos;
using DeNoise.Application.Mapping;
using DeNoise.Domain.Heartbeats;

namespace DeNoise.Application.Heartbeats;

/// <summary>
/// <c>expected_next</c> (04 §10): interval schedules ⇒ <c>last_ping + interval</c>; cron ⇒ next occurrence after the ping in the
/// declared IANA zone (Cronos, ADR-8). Cronos skips the wall-clock time that does not exist on a spring-forward night and fires
/// once on the repeated autumn hour, which is exactly the spec §13.3.3 rule.
/// </summary>
public static class HeartbeatSchedule
{
    public static DateTimeOffset? NextAfter(Heartbeat hb, DateTimeOffset from)
        => NextAfter(hb.ScheduleKind, hb.Interval, hb.Cron, hb.ScheduleTz, from);

    public static DateTimeOffset? NextAfter(string kind, TimeSpan? interval, string? cron, string? tz, DateTimeOffset from)
    {
        if (kind == ScheduleKinds.Interval) return interval is { } i && i > TimeSpan.Zero ? from + i : null;
        if (string.IsNullOrWhiteSpace(cron)) return null;
        var expression = CronExpression.Parse(cron, cron.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard);
        var zone = ResolveZone(tz);
        // Cronos answers in the zone's offset; storage and the API are UTC only (ADR-8).
        return expression.GetNextOccurrence(from, zone)?.ToUniversalTime();
    }

    /// <summary>The next <paramref name="count"/> runs, for the "next 5 runs" preview in the form (08 §3.4).</summary>
    public static IReadOnlyList<DateTimeOffset> Preview(string kind, TimeSpan? interval, string? cron, string? tz, DateTimeOffset from, int count)
    {
        var list = new List<DateTimeOffset>();
        var cursor = from;
        for (var i = 0; i < count; i++)
        {
            var next = NextAfter(kind, interval, cron, tz, cursor);
            if (next is null || next <= cursor) break;
            list.Add(next.Value);
            cursor = next.Value;
        }
        return list;
    }

    /// <summary>Human wording for lists: "every 5 min", "cron 30 2 * * * (Europe/Warsaw)".</summary>
    public static string Describe(Heartbeat hb)
    {
        if (hb.ScheduleKind == ScheduleKinds.Interval && hb.Interval is { } i)
        {
            if (i.TotalSeconds < 60) return $"every {i.TotalSeconds:F0} s";
            if (i.TotalMinutes < 60) return $"every {i.TotalMinutes:F0} min";
            if (i.TotalHours < 24) return i.TotalHours == 1 ? "hourly" : $"every {i.TotalHours:F0} h";
            return i.TotalDays == 1 ? "daily" : $"every {i.TotalDays:F0} d";
        }
        return $"cron {hb.Cron} ({hb.ScheduleTz})";
    }

    public static void Validate(string kind, TimeSpan? interval, string? cron, string? tz, TimeSpan grace, List<MappingValidationError> errors)
    {
        switch (kind)
        {
            case ScheduleKinds.Interval:
                if (interval is null || interval <= TimeSpan.Zero) errors.Add(new MappingValidationError("$.schedule.interval", "an interval schedule needs a positive interval"));
                else if (interval < TimeSpan.FromSeconds(30)) errors.Add(new MappingValidationError("$.schedule.interval", "intervals shorter than 30 s are not supported"));
                break;
            case ScheduleKinds.Cron:
                if (string.IsNullOrWhiteSpace(cron)) errors.Add(new MappingValidationError("$.schedule.cron", "a cron schedule needs an expression"));
                else
                {
                    try
                    {
                        CronExpression.Parse(cron, cron.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard);
                    }
                    catch (CronFormatException ex)
                    {
                        errors.Add(new MappingValidationError("$.schedule.cron", $"invalid cron expression: {ex.Message}"));
                    }
                }
                if (string.IsNullOrWhiteSpace(tz)) errors.Add(new MappingValidationError("$.schedule.timezone", "cron schedules require an IANA timezone (spec §13.3.1)"));
                else if (!TryResolveZone(tz, out _)) errors.Add(new MappingValidationError("$.schedule.timezone", $"unknown timezone '{tz}' (use an IANA id such as Europe/Warsaw)"));
                break;
            default:
                errors.Add(new MappingValidationError("$.schedule.kind", "must be interval or cron"));
                break;
        }
        if (grace < TimeSpan.Zero) errors.Add(new MappingValidationError("$.grace", "must not be negative"));
    }

    public static TimeZoneInfo ResolveZone(string? tz)
        => TryResolveZone(tz, out var zone) ? zone : TimeZoneInfo.Utc;

    public static bool TryResolveZone(string? tz, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(tz)) return false;
        if (string.Equals(tz, "UTC", StringComparison.OrdinalIgnoreCase)) return true;
        return TimeZoneInfo.TryFindSystemTimeZoneById(tz, out var found) && (zone = found) is not null;
    }
}
