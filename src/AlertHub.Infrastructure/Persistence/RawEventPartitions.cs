using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AlertHub.Infrastructure.Persistence;

/// <summary>
/// Daily partitions of <c>alert.raw_event</c> (05 §2, §7). Creation is idempotent and runs from the Migrator
/// after migrating and from the scheduler's <c>partition_create</c> job; dropping is the <c>retention_raw</c> job.
/// </summary>
public sealed class RawEventPartitions(AlertHubDbContext db, TimeProvider time, ILogger<RawEventPartitions> logger)
{
    public const int DefaultDaysAhead = 7;
    private const string Prefix = "raw_event_p";

    public static string PartitionName(DateOnly day) => Prefix + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    public static bool TryParsePartitionDay(string tableName, out DateOnly day)
    {
        day = default;
        return tableName.StartsWith(Prefix, StringComparison.Ordinal)
            && DateOnly.TryParseExact(tableName[Prefix.Length..], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }

    /// <summary>Ensures partitions exist from yesterday through <paramref name="daysAhead"/> days after today (UTC).</summary>
    public async Task<int> EnsureAsync(int daysAhead = DefaultDaysAhead, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var created = 0;
        for (var d = today.AddDays(-1); d <= today.AddDays(daysAhead); d = d.AddDays(1))
        {
            if (await EnsureDayAsync(d, ct)) created++;
        }
        return created;
    }

    public async Task<bool> EnsureDayAsync(DateOnly day, CancellationToken ct = default)
    {
        var name = PartitionName(day);
        var from = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var to = day.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var existed = await db.Database
            .SqlQueryRaw<int>("SELECT 1 AS \"Value\" FROM pg_tables WHERE schemaname = 'alert' AND tablename = {0}", name)
            .AnyAsync(ct);
        if (existed) return false;

        // Identifiers are derived from a DateOnly and a fixed prefix, never from user input.
#pragma warning disable EF1002
        await db.Database.ExecuteSqlRawAsync(
            $"CREATE TABLE IF NOT EXISTS alert.\"{name}\" PARTITION OF alert.raw_event FOR VALUES FROM ('{from} 00:00:00+00') TO ('{to} 00:00:00+00')", ct);
#pragma warning restore EF1002
        logger.LogInformation("Created raw_event partition {Partition}", name);
        return true;
    }

    public async Task<IReadOnlyList<DateOnly>> ListAsync(CancellationToken ct = default)
    {
        var names = await db.Database
            .SqlQueryRaw<string>("SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname = 'alert' AND tablename LIKE 'raw_event_p%'")
            .ToListAsync(ct);
        return names.Select(n => TryParsePartitionDay(n, out var d) ? d : (DateOnly?)null)
            .Where(d => d.HasValue).Select(d => d!.Value).OrderBy(d => d).ToList();
    }
}
