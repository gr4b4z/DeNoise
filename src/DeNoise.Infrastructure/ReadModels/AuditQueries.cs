using System.Globalization;
using System.Text;
using DeNoise.Contracts;
using DeNoise.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DeNoise.Infrastructure.ReadModels;

public sealed record AuditFilter(string? TargetType, string? TargetId, string? Actor, string? Action, DateTimeOffset? From, DateTimeOffset? To, int Limit, string? Cursor);

/// <summary>06 §4 <c>GET /api/v1/audit</c>: newest first, keyset-paged on (at, id); filters map onto the <c>audit_target_idx</c> / <c>audit_actor_idx</c> / <c>audit_at_idx</c> indexes (05 §9).</summary>
public sealed class AuditQueries(DeNoiseDbContext db)
{
    public async Task<PagedResponse<AuditEntryDto>> ListAsync(AuditFilter filter, CancellationToken ct = default)
    {
        var limit = Math.Clamp(filter.Limit, 1, 500);
        IQueryable<Domain.Audit.AuditEntry> q = db.AuditEntries.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(filter.TargetType)) q = q.Where(a => a.TargetType == filter.TargetType);
        if (!string.IsNullOrWhiteSpace(filter.TargetId)) q = q.Where(a => a.TargetId == filter.TargetId);
        if (!string.IsNullOrWhiteSpace(filter.Actor))
        {
            var actor = filter.Actor;
            q = q.Where(a => a.ActorId == actor || a.ActorDisplay == actor);
        }
        if (!string.IsNullOrWhiteSpace(filter.Action))
        {
            var action = filter.Action;
            q = q.Where(a => a.Action == action || a.Action.StartsWith(action + "."));
        }
        if (filter.From is { } from) q = q.Where(a => a.At >= from);
        if (filter.To is { } to) q = q.Where(a => a.At < to);
        if (TryDecode(filter.Cursor, out var at, out var id)) q = q.Where(a => a.At < at || (a.At == at && a.Id.CompareTo(id) < 0));

        var rows = await q.OrderByDescending(a => a.At).ThenByDescending(a => a.Id).Take(limit + 1).ToListAsync(ct);
        var more = rows.Count > limit;
        if (more) rows.RemoveAt(rows.Count - 1);
        var items = rows.Select(a => new AuditEntryDto(a.Id, a.At, a.ActorType, a.ActorId, a.ActorDisplay, a.Action, a.TargetType, a.TargetId, a.AccessScope,
            Parse(a.Before), Parse(a.After), a.Reason, a.CorrelationId, a.RequestIp?.ToString())).ToList();
        return new PagedResponse<AuditEntryDto>(items, more ? Encode(rows[^1].At, rows[^1].Id) : null, null);
    }

    private static System.Text.Json.JsonElement? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string Encode(DateTimeOffset at, Guid id) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{at.UtcTicks}|{id:N}"));

    private static bool TryDecode(string? cursor, out DateTimeOffset at, out Guid id)
    {
        at = default;
        id = default;
        if (string.IsNullOrEmpty(cursor)) return false;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
            if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || !Guid.TryParseExact(parts[1], "N", out id)) return false;
            at = new DateTimeOffset(ticks, TimeSpan.Zero);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
