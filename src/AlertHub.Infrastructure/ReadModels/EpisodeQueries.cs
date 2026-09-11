using System.Globalization;
using System.Text;
using System.Text.Json;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Auth;
using AlertHub.Application.Episodes;
using AlertHub.Contracts;
using AlertHub.Domain.Alerts;
using AlertHub.Domain.Common;
using AlertHub.Domain.Episodes;
using AlertHub.Domain.Ops;
using AlertHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AlertHub.Infrastructure.ReadModels;

/// <summary>
/// Work-queue read model (ADR-4): hand-written SQL over <c>alert.episode</c> with joins to team, user and integration,
/// keyset pagination on (severity rank, ack overdue, last_seen, id) and scope enforcement in every WHERE clause.
/// </summary>
public sealed class EpisodeQueries(AlertHubDbContext db, NpgsqlDataSource dataSource, TimeProvider time) : IEpisodeQueries
{
    private const string SeverityRank = "CASE e.severity WHEN 'critical' THEN 5 WHEN 'unknown' THEN 4 WHEN 'high' THEN 3 WHEN 'medium' THEN 2 WHEN 'low' THEN 1 ELSE 0 END";

    private const string SelectColumns = """
        SELECT e.episode_id, e.severity, e.summary, e.resource_name, e.service, e.environment, e.condition_state, e.handling_state,
               e.owning_team_id, t.name AS team_name, e.assignee_id, u.username AS assignee_username, u.display_name AS assignee_display,
               e.first_seen, e.last_seen, e.occurrence_count, e.ack_deadline_at, e.auto_resolve_at, e.suppressed_until, e.group_id,
               e.routing_correction_required, e.integration_id, coalesce(i.name, '') AS integration_name, e.access_scope, e.is_actionable,
               e.closed_at, e.closure_reason, e.resolution_evidence, e.version,
               coalesce((i.capabilities->>'retransmission_indistinguishable')::bool, false) AS retrans,
               (SELECT min(j.not_before) FROM ops.job j WHERE j.episode_id = e.episode_id AND j.kind = 'escalation_step' AND j.status = 'pending') AS next_escalation,
               EXISTS (SELECT 1 FROM ops.job j WHERE j.episode_id = e.episode_id AND j.kind = 'auto_resolve' AND j.status = 'suspended') AS auto_suspended,
               EXISTS (SELECT 1 FROM ops.outbox o WHERE o.episode_id = e.episode_id AND o.status = 'failed') AS delivery_failure,
               coalesce(cs.state, 'unknown') AS coverage_state
        FROM alert.episode e
        LEFT JOIN cfg.team t ON t.team_id = e.owning_team_id
        LEFT JOIN cfg."user" u ON u.user_id = e.assignee_id
        LEFT JOIN cfg.integration i ON i.integration_id = e.integration_id AND i.deactivated_at IS NULL
        LEFT JOIN ops.coverage_state cs ON cs.integration_id = e.integration_id
        """;

    public async Task<PagedResponse<EpisodeListItem>> ListAsync(AlertHubPrincipal principal, EpisodeFilter filter, IReadOnlyCollection<Guid> myTeamIds, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var where = new List<string>();
        var parameters = new List<NpgsqlParameter>();
        ScopeClause(principal, where, parameters);

        var view = filter.View ?? QueueViews.NeedsAttention;
        switch (view)
        {
            case QueueViews.NeedsAttention:
                where.Add("e.handling_state <> 'closed' AND e.is_actionable AND (e.suppressed_until IS NULL OR e.suppressed_until < @now)");
                break;
            case QueueViews.Mine:
                where.Add("e.handling_state <> 'closed' AND e.assignee_id = @me");
                parameters.Add(new NpgsqlParameter("me", principal.UserId));
                break;
            case QueueViews.MyTeams:
                where.Add("e.handling_state <> 'closed' AND e.owning_team_id = ANY(@teams)");
                parameters.Add(new NpgsqlParameter("teams", myTeamIds.ToArray()));
                break;
            case QueueViews.Unassigned:
                where.Add("e.handling_state <> 'closed' AND e.assignee_id IS NULL");
                break;
            case QueueViews.Acknowledged:
                where.Add("e.handling_state = 'acknowledged'");
                break;
            case QueueViews.Stale:
                where.Add("((e.handling_state <> 'closed' AND e.condition_state = 'unknown') OR (e.handling_state = 'closed' AND e.closure_reason = 'expired_unverified'))");
                break;
            case QueueViews.Suppressed:
                where.Add("e.handling_state <> 'closed' AND e.suppressed_until IS NOT NULL AND e.suppressed_until >= @now");
                break;
            case QueueViews.Closed:
                where.Add("e.handling_state = 'closed'");
                break;
            default:
                throw new ArgumentException($"unknown view '{view}'");
        }
        parameters.Add(new NpgsqlParameter("now", now));

        AddIn(filter.Severity, "e.severity", "sev", where, parameters, Severities);
        AddIn(filter.Handling, "e.handling_state", "hs", where, parameters, [HandlingState.New, HandlingState.Acknowledged, HandlingState.Closed]);
        AddIn(filter.Condition, "e.condition_state", "cs", where, parameters, [ConditionState.Firing, ConditionState.Resolved, ConditionState.Unknown, ConditionState.NotApplicable]);
        if (filter.TeamId is { } team) { where.Add("e.owning_team_id = @team"); parameters.Add(new NpgsqlParameter("team", team)); }
        if (!string.IsNullOrWhiteSpace(filter.Scope)) { where.Add("e.access_scope = @scope"); parameters.Add(new NpgsqlParameter("scope", filter.Scope)); }
        if (!string.IsNullOrWhiteSpace(filter.Environment)) { where.Add("e.environment = @env"); parameters.Add(new NpgsqlParameter("env", filter.Environment)); }
        if (!string.IsNullOrWhiteSpace(filter.Service)) { where.Add("e.service = @svc"); parameters.Add(new NpgsqlParameter("svc", filter.Service)); }
        if (filter.IntegrationId is { } integration) { where.Add("e.integration_id = @integration"); parameters.Add(new NpgsqlParameter("integration", integration)); }
        if (!string.IsNullOrWhiteSpace(filter.ClosureReason)) { where.Add("e.closure_reason = @closure"); parameters.Add(new NpgsqlParameter("closure", filter.ClosureReason)); }
        if (!string.IsNullOrWhiteSpace(filter.Evidence)) { where.Add("e.resolution_evidence = @evidence"); parameters.Add(new NpgsqlParameter("evidence", filter.Evidence)); }
        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            where.Add("(e.search_tsv @@ plainto_tsquery('simple', @q) OR e.summary ILIKE @qlike OR e.resource_name ILIKE @qlike)");
            parameters.Add(new NpgsqlParameter("q", filter.Query.Trim()));
            parameters.Add(new NpgsqlParameter("qlike", "%" + filter.Query.Trim() + "%"));
        }

        var (orderBy, cursorClause) = Order(filter.Sort, filter.Cursor, parameters, now);
        if (cursorClause is not null) where.Add(cursorClause);
        var limit = Math.Clamp(filter.Limit, 0, 200);

        var sql = new StringBuilder(SelectColumns).Append(" WHERE ").Append(string.Join(" AND ", where)).Append(" ORDER BY ").Append(orderBy).Append(" LIMIT @limit");
        parameters.Add(new NpgsqlParameter("limit", limit + 1));

        var items = new List<EpisodeListItem>();
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        await using (var cmd = new NpgsqlCommand(sql.ToString(), conn))
        {
            foreach (var p in parameters) cmd.Parameters.Add(p);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) items.Add(Map(reader, now));
        }

        string? nextCursor = null;
        if (items.Count > limit)
        {
            items.RemoveAt(items.Count - 1);
            var last = items[^1];
            nextCursor = EncodeCursor(last, now);
        }

        int? total = null;
        if (filter.IncludeTotal)
        {
            var countWhere = where.Where(w => w != cursorClause).ToList();
            var countSql = "SELECT count(*) FROM alert.episode e LEFT JOIN cfg.integration i ON i.integration_id = e.integration_id AND i.deactivated_at IS NULL WHERE " + string.Join(" AND ", countWhere) + " LIMIT 1";
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(countSql, conn);
            foreach (var p in parameters.Where(p => p.ParameterName is not ("limit" or "c_rank" or "c_overdue" or "c_last" or "c_id"))) cmd.Parameters.Add(p.Clone());
            total = (int)Math.Min(10_000, (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L));
        }
        return new PagedResponse<EpisodeListItem>(items, nextCursor, total);
    }

    public async Task<EpisodeDetail?> GetAsync(AlertHubPrincipal principal, Guid episodeId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        EpisodeListItem? item = null;
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        await using (var cmd = new NpgsqlCommand(SelectColumns + " WHERE e.episode_id = @id", conn))
        {
            cmd.Parameters.AddWithValue("id", episodeId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) item = Map(reader, now);
        }
        if (item is null || !principal.CanSeeScope(item.AccessScope)) return null;

        var episode = await db.Episodes.AsNoTracking().SingleAsync(e => e.EpisodeId == episodeId, ct);
        var identity = await db.Identities.AsNoTracking().SingleOrDefaultAsync(i => i.Fingerprint == episode.Fingerprint, ct);
        var components = identity is null ? [] : JsonSerializer.Deserialize<List<IdentityComponentDto>>(identity.Components, JsonDefaults.Stored) ?? [];
        var timers = await db.Jobs.AsNoTracking()
            .Where(j => j.EpisodeId == episodeId && (j.Status == JobStatus.Pending || j.Status == JobStatus.Suspended || j.Status == JobStatus.Reserved))
            .OrderBy(j => j.NotBefore).Select(j => new TimerDto(j.Kind, j.NotBefore, j.Status)).ToListAsync(ct);
        var routingEvent = await db.EpisodeEvents.AsNoTracking().Where(e => e.EpisodeId == episodeId && e.Kind == EpisodeEventKind.Assign && e.EventId != null)
            .OrderBy(e => e.At).Select(e => e.Detail).FirstOrDefaultAsync(ct);
        RoutingDto routing = new(episode.RoutingRuleId, null, null);
        if (routingEvent is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(routingEvent);
                routing = new RoutingDto(episode.RoutingRuleId, doc.RootElement.TryGetProperty("ruleName", out var rn) ? rn.GetString() : null, doc.RootElement.TryGetProperty("why", out var w) ? w.GetString() : null);
            }
            catch (JsonException) { /* keep defaults */ }
        }
        var confidence = await db.NormalisedEvents.AsNoTracking().Where(n => n.EpisodeId == episodeId).OrderByDescending(n => n.ReceivedAt).Select(n => n.IdentityConfidence).FirstOrDefaultAsync(ct);
        var oldestRaw = await db.NormalisedEvents.AsNoTracking().Where(n => n.EpisodeId == episodeId).MinAsync(n => (DateTimeOffset?)n.RawReceivedAt, ct);
        var rawAvailable = oldestRaw is { } r && await RawPartitionExistsAsync(r, ct);
        UserRef? ackBy = null;
        if (episode.AcknowledgedBy is { } by) ackBy = await UserRefAsync(by, ct);
        ClosureDto? closure = episode.ClosureReason is null ? null : new ClosureDto(episode.ClosureReason, episode.ResolutionEvidence, episode.ClosureNote, episode.ClosedAt, null, episode.RestoredFromReason);

        return new EpisodeDetail(item, episode.SourceAlertId, episode.Fingerprint, components,
            new LifecycleDto(episode.LifecycleProfile, episode.LifecyclePolicyVersion, episode.AutoResolveAt, item.AutoResolveSuspended),
            routing, closure, episode.SourceUrl, episode.RunbookUrl, rawAvailable, episode.PreviousEpisodeId, timers,
            EpisodeExplanation.Build(episode, item, routing, timers), episode.AcknowledgedAt, ackBy, confidence);
    }

    public async Task<PagedResponse<TimelineEntry>> TimelineAsync(AlertHubPrincipal principal, Guid episodeId, int limit, string? cursor, string? kind, CancellationToken ct = default)
    {
        var scope = await db.Episodes.AsNoTracking().Where(e => e.EpisodeId == episodeId).Select(e => e.AccessScope).SingleOrDefaultAsync(ct);
        if (scope is null || !principal.CanSeeScope(scope)) return new PagedResponse<TimelineEntry>([], null, null);
        limit = Math.Clamp(limit, 1, 200);
        var before = cursor is not null && DateTimeOffset.TryParse(cursor, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var c) ? c : (DateTimeOffset?)null;

        var events = db.EpisodeEvents.AsNoTracking().Where(e => e.EpisodeId == episodeId);
        if (kind is not null) events = events.Where(e => e.Kind == kind);
        if (before is not null) events = events.Where(e => e.At < before);
        var eventRows = await events.OrderByDescending(e => e.At).Take(limit + 1).ToListAsync(ct);

        var deliveries = kind is null or "notify"
            ? await (from o in db.Outbox.AsNoTracking()
                     join d in db.Destinations.AsNoTracking() on o.DestinationId equals d.DestinationId into dj
                     from d in dj.DefaultIfEmpty()
                     where o.EpisodeId == episodeId && (before == null || o.CreatedAt < before)
                     orderby o.CreatedAt descending
                     select new { o.OutboxId, o.CreatedAt, o.Type, o.Status, o.SentAt, o.Attempts, o.LastError, DestinationName = d != null ? d.Name : null })
                .Take(limit + 1).ToListAsync(ct)
            : [];

        var actorIds = eventRows.Where(e => e.ActorId != null).Select(e => e.ActorId!.Value).Distinct().ToList();
        var actors = await db.Users.AsNoTracking().Where(u => actorIds.Contains(u.UserId)).ToDictionaryAsync(u => u.UserId, u => new UserRef(u.UserId, u.Username, u.DisplayName), ct);

        var merged = eventRows.Select(e => new TimelineEntry(e.Id, e.At, e.Kind, "episode", e.ActorId is { } a && actors.TryGetValue(a, out var u) ? u : null, e.EventId, e.Detail, null, null))
            .Concat(deliveries.Select(d => new TimelineEntry(d.OutboxId, d.CreatedAt, EpisodeEventKind.Notify, "delivery", null, null,
                JsonSerializer.Serialize(new { type = d.Type, attempts = d.Attempts, sentAt = d.SentAt, error = d.LastError }, JsonDefaults.Stored), d.Status, d.DestinationName)))
            .OrderByDescending(t => t.At).ThenBy(t => t.Id).Take(limit + 1).ToList();
        string? next = null;
        if (merged.Count > limit)
        {
            merged.RemoveAt(merged.Count - 1);
            next = merged[^1].At.ToString("O");
        }
        return new PagedResponse<TimelineEntry>(merged, next, null);
    }

    public async Task<RelatedEpisodes?> RelatedAsync(AlertHubPrincipal principal, Guid episodeId, CancellationToken ct = default)
    {
        var episode = await db.Episodes.AsNoTracking().SingleOrDefaultAsync(e => e.EpisodeId == episodeId, ct);
        if (episode is null || !principal.CanSeeScope(episode.AccessScope)) return null;
        var next = await db.Episodes.AsNoTracking().Where(e => e.PreviousEpisodeId == episodeId).Select(e => (Guid?)e.EpisodeId).FirstOrDefaultAsync(ct);
        var same = await ListByIdsAsync(await db.Episodes.AsNoTracking().Where(e => e.Fingerprint == episode.Fingerprint && e.EpisodeId != episodeId)
            .OrderByDescending(e => e.CreatedAt).Take(20).Select(e => e.EpisodeId).ToListAsync(ct), ct);
        var group = episode.GroupId is { } g
            ? await ListByIdsAsync(await db.Episodes.AsNoTracking().Where(e => e.GroupId == g && e.EpisodeId != episodeId).Take(50).Select(e => e.EpisodeId).ToListAsync(ct), ct)
            : [];
        return new RelatedEpisodes(episode.PreviousEpisodeId, next, same, group);
    }

    public async Task<(byte[] Body, string? ContentType, DateTimeOffset ReceivedAt)?> RawPayloadAsync(AlertHubPrincipal principal, Guid episodeId, Guid eventId, CancellationToken ct = default)
    {
        if (!principal.Has(Domain.Users.Permissions.EpisodeRawPayloadRead)) throw new ForbiddenException(Domain.Users.Permissions.EpisodeRawPayloadRead);
        var scope = await db.Episodes.AsNoTracking().Where(e => e.EpisodeId == episodeId).Select(e => e.AccessScope).SingleOrDefaultAsync(ct);
        if (scope is null || !principal.CanSeeScope(scope)) return null;
        var normalised = await db.NormalisedEvents.AsNoTracking().SingleOrDefaultAsync(n => n.EventId == eventId && n.EpisodeId == episodeId, ct);
        if (normalised is null) return null;
        var raw = await db.RawEvents.AsNoTracking().SingleOrDefaultAsync(r => r.ReceivedAt == normalised.RawReceivedAt && r.EventId == eventId, ct);
        return raw is null ? null : (raw.Body, raw.ContentType, raw.ReceivedAt);
    }

    public async Task<IReadOnlyDictionary<string, int>> ViewCountsAsync(AlertHubPrincipal principal, IReadOnlyCollection<Guid> myTeamIds, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var where = new List<string>();
        var parameters = new List<NpgsqlParameter>();
        ScopeClause(principal, where, parameters);
        parameters.Add(new NpgsqlParameter("now", now));
        parameters.Add(new NpgsqlParameter("me", principal.UserId));
        parameters.Add(new NpgsqlParameter("teams", myTeamIds.ToArray()));
        var sql = $"""
            SELECT
              count(*) FILTER (WHERE e.handling_state <> 'closed' AND e.is_actionable AND (e.suppressed_until IS NULL OR e.suppressed_until < @now)) AS needs_attention,
              count(*) FILTER (WHERE e.handling_state <> 'closed' AND e.assignee_id = @me) AS mine,
              count(*) FILTER (WHERE e.handling_state <> 'closed' AND e.owning_team_id = ANY(@teams)) AS my_teams,
              count(*) FILTER (WHERE e.handling_state <> 'closed' AND e.assignee_id IS NULL) AS unassigned,
              count(*) FILTER (WHERE e.handling_state = 'acknowledged') AS acknowledged,
              count(*) FILTER (WHERE (e.handling_state <> 'closed' AND e.condition_state = 'unknown') OR (e.handling_state = 'closed' AND e.closure_reason = 'expired_unverified')) AS stale,
              count(*) FILTER (WHERE e.handling_state <> 'closed' AND e.suppressed_until >= @now) AS suppressed
            FROM alert.episode e WHERE {string.Join(" AND ", where)}
            """;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var p in parameters) cmd.Parameters.Add(p);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new Dictionary<string, int>
        {
            [QueueViews.NeedsAttention] = (int)reader.GetInt64(0),
            [QueueViews.Mine] = (int)reader.GetInt64(1),
            [QueueViews.MyTeams] = (int)reader.GetInt64(2),
            [QueueViews.Unassigned] = (int)reader.GetInt64(3),
            [QueueViews.Acknowledged] = (int)reader.GetInt64(4),
            [QueueViews.Stale] = (int)reader.GetInt64(5),
            [QueueViews.Suppressed] = (int)reader.GetInt64(6),
        };
    }

    public async Task<TeamOverview?> TeamOverviewAsync(AlertHubPrincipal principal, Guid teamId, CancellationToken ct = default)
    {
        var team = await db.Teams.AsNoTracking().SingleOrDefaultAsync(t => t.TeamId == teamId, ct);
        if (team is null) return null;
        var members = await db.TeamMembers.CountAsync(m => m.TeamId == teamId, ct);
        var now = time.GetUtcNow();
        var open = db.Episodes.AsNoTracking().Where(e => e.OwningTeamId == teamId && e.HandlingState != HandlingState.Closed && (principal.IsPlatformAdmin || principal.Scopes.Contains(e.AccessScope)));
        var unassigned = await open.CountAsync(e => e.AssigneeId == null, ct);
        var overdue = await open.CountAsync(e => e.HandlingState == HandlingState.New && e.AckDeadlineAt != null && e.AckDeadlineAt < now, ct);
        var acknowledged = await open.CountAsync(e => e.HandlingState == HandlingState.Acknowledged, ct);
        var stale = await open.CountAsync(e => e.ConditionState == ConditionState.Unknown, ct);
        var total = await open.CountAsync(ct);
        var unassignedItems = await ListByIdsAsync(await open.Where(e => e.AssigneeId == null).OrderByDescending(e => e.LastSeen).Take(10).Select(e => e.EpisodeId).ToListAsync(ct), ct);
        var overdueItems = await ListByIdsAsync(await open.Where(e => e.HandlingState == HandlingState.New && e.AckDeadlineAt != null && e.AckDeadlineAt < now).OrderBy(e => e.AckDeadlineAt).Take(10).Select(e => e.EpisodeId).ToListAsync(ct), ct);
        var summary = new TeamSummary(team.TeamId, team.Name, team.AccessScopes, team.IsTriage, team.FallbackTeamId, team.DefaultEscalationPolicyId, members, team.Version);
        return new TeamOverview(summary, unassigned, overdue, acknowledged, stale, total, unassignedItems, overdueItems);
    }

    private async Task<IReadOnlyList<EpisodeListItem>> ListByIdsAsync(List<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var now = time.GetUtcNow();
        var items = new List<EpisodeListItem>();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(SelectColumns + " WHERE e.episode_id = ANY(@ids) ORDER BY e.last_seen DESC", conn);
        cmd.Parameters.AddWithValue("ids", ids.ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) items.Add(Map(reader, now));
        return items;
    }

    private async Task<UserRef?> UserRefAsync(Guid userId, CancellationToken ct)
        => await db.Users.AsNoTracking().Where(u => u.UserId == userId).Select(u => new UserRef(u.UserId, u.Username, u.DisplayName)).SingleOrDefaultAsync(ct);

    private async Task<bool> RawPartitionExistsAsync(DateTimeOffset receivedAt, CancellationToken ct)
    {
        var name = RawEventPartitions.PartitionName(DateOnly.FromDateTime(receivedAt.UtcDateTime));
        return await db.Database.SqlQueryRaw<int>("SELECT 1 AS \"Value\" FROM pg_tables WHERE schemaname = 'alert' AND tablename = {0}", name).AnyAsync(ct);
    }

    private static void ScopeClause(AlertHubPrincipal principal, List<string> where, List<NpgsqlParameter> parameters)
    {
        if (principal.IsPlatformAdmin) return;
        where.Add("e.access_scope = ANY(@scopes)");
        parameters.Add(new NpgsqlParameter("scopes", principal.Scopes.ToArray()));
    }

    private static readonly string[] Severities = ["critical", "high", "medium", "low", "informational", "unknown"];

    private static void AddIn(IReadOnlyList<string>? values, string column, string name, List<string> where, List<NpgsqlParameter> parameters, string[] allowed)
    {
        if (values is null || values.Count == 0) return;
        var clean = values.Select(v => v.Trim().ToLowerInvariant()).Where(allowed.Contains).Distinct().ToArray();
        if (clean.Length == 0) { where.Add("false"); return; }
        where.Add($"{column} = ANY(@{name})");
        parameters.Add(new NpgsqlParameter(name, clean));
    }

    /// <summary>Default order (08 §3.1): severity rank desc, ack-overdue first, last_seen desc, id for stability. Keyset cursor over the same tuple.</summary>
    private static (string OrderBy, string? CursorClause) Order(string? sort, string? cursor, List<NpgsqlParameter> parameters, DateTimeOffset now)
    {
        var overdue = "(CASE WHEN e.handling_state = 'new' AND e.ack_deadline_at IS NOT NULL AND e.ack_deadline_at < @now THEN 1 ELSE 0 END)";
        string orderBy;
        string? clause = null;
        var lastSeenAsc = sort is not null && sort.Split(',').Any(s => s.Trim() == "lastSeen");
        if (lastSeenAsc)
        {
            orderBy = "e.last_seen ASC, e.episode_id ASC";
            if (TryDecodeCursor(cursor, out var c))
            {
                clause = "(e.last_seen, e.episode_id) > (@c_last, @c_id)";
                parameters.Add(new NpgsqlParameter("c_last", c.LastSeen));
                parameters.Add(new NpgsqlParameter("c_id", c.Id));
            }
            return (orderBy, clause);
        }
        orderBy = $"{SeverityRank} DESC, {overdue} DESC, e.last_seen DESC, e.episode_id DESC";
        if (TryDecodeCursor(cursor, out var cur))
        {
            clause = $"({SeverityRank}, {overdue}, e.last_seen, e.episode_id) < (@c_rank, @c_overdue, @c_last, @c_id)";
            parameters.Add(new NpgsqlParameter("c_rank", cur.Rank));
            parameters.Add(new NpgsqlParameter("c_overdue", cur.Overdue));
            parameters.Add(new NpgsqlParameter("c_last", cur.LastSeen));
            parameters.Add(new NpgsqlParameter("c_id", cur.Id));
        }
        return (orderBy, clause);
    }

    private static int Rank(string severity) => severity switch { "critical" => 5, "unknown" => 4, "high" => 3, "medium" => 2, "low" => 1, _ => 0 };

    private static string EncodeCursor(EpisodeListItem last, DateTimeOffset now)
    {
        var overdue = last.HandlingState == HandlingState.New && last.AckDeadlineAt is { } d && d < now ? 1 : 0;
        var text = $"{Rank(last.Severity)}|{overdue}|{last.LastSeen:O}|{last.Id}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryDecodeCursor(string? cursor, out (int Rank, int Overdue, DateTimeOffset LastSeen, Guid Id) result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(cursor)) return false;
        try
        {
            var b64 = cursor.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Split('|');
            if (parts.Length != 4) return false;
            result = (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(parts[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), Guid.Parse(parts[3]));
            return true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            return false;
        }
    }

    private static EpisodeListItem Map(NpgsqlDataReader r, DateTimeOffset now)
    {
        DateTimeOffset? Dto(int i) => r.IsDBNull(i) ? null : new DateTimeOffset(r.GetDateTime(i), TimeSpan.Zero);
        string? Str(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        Guid? Gid(int i) => r.IsDBNull(i) ? null : r.GetGuid(i);
        var handling = r.GetString(7);
        var ackDeadline = Dto(16);
        var teamId = Gid(8);
        var assigneeId = Gid(10);
        return new EpisodeListItem(
            r.GetGuid(0), r.GetString(1), Str(2), Str(3), Str(4), Str(5), r.GetString(6), handling,
            teamId is { } t ? new TeamRef(t, Str(9) ?? string.Empty) : null,
            assigneeId is { } a ? new UserRef(a, Str(11) ?? string.Empty, Str(12) ?? string.Empty) : null,
            new DateTimeOffset(r.GetDateTime(13), TimeSpan.Zero), new DateTimeOffset(r.GetDateTime(14), TimeSpan.Zero), r.GetInt32(15), !r.GetBoolean(29),
            ackDeadline, handling == HandlingState.New && ackDeadline is { } d && d < now, Dto(30), Dto(17), r.GetBoolean(31),
            r.GetString(33), r.GetString(6) == ConditionState.Unknown, Dto(18), r.GetBoolean(32), Gid(19), r.GetBoolean(20),
            r.GetGuid(21), r.GetString(22), r.GetString(23), r.GetBoolean(24), Dto(25), Str(26), Str(27), r.GetInt32(28));
    }
}

/// <summary>Plain-language paragraph explaining automation on the episode (08 §1 "Explain automation"). Built server-side so every client says the same thing.</summary>
public static class EpisodeExplanation
{
    public static string Build(Episode episode, EpisodeListItem item, RoutingDto routing, IReadOnlyList<TimerDto> timers)
    {
        var sb = new StringBuilder();
        sb.Append(episode.Severity.ToWire()).Append(" alert, condition ").Append(episode.ConditionState).Append(", handling ").Append(episode.HandlingState).Append(". ");
        sb.Append("Seen ").Append(episode.OccurrenceCount).Append(episode.OccurrenceCount == 1 ? " time" : " times").Append(" since ").Append(episode.FirstSeen.ToString("u", CultureInfo.InvariantCulture)).Append(". ");
        if (routing.RuleName is not null) sb.Append("Routed to ").Append(item.OwningTeam?.Name ?? "a team").Append(" by rule '").Append(routing.RuleName).Append("'. ");
        else if (episode.RoutingCorrectionRequired) sb.Append("No routing rule matched; sent to the triage team and flagged for routing correction. ");
        if (episode.HandlingState == HandlingState.New && episode.AckDeadlineAt is { } deadline) sb.Append("Acknowledgement is due by ").Append(deadline.ToString("u", CultureInfo.InvariantCulture)).Append(". ");
        var autoResolve = timers.FirstOrDefault(t => t.Kind == JobKinds.AutoResolve);
        if (autoResolve is not null)
        {
            sb.Append(autoResolve.Status == JobStatus.Suspended
                ? "Automatic resolution is suspended because coverage of the source is degraded. "
                : $"Will resolve automatically at {autoResolve.At.ToString("u", CultureInfo.InvariantCulture)} if no further signal arrives. ");
        }
        else if (episode.IsOpen) sb.Append("No automatic resolution is scheduled; it closes when the source reports recovery or an operator closes it. ");
        if (episode.ClosureReason is not null)
        {
            sb.Append("Closed as ").Append(episode.ClosureReason).Append(" with evidence ").Append(episode.ResolutionEvidence ?? Evidence.None).Append(". ");
            if (episode.ResolutionEvidence == Evidence.InactivityUnverified) sb.Append("Recovery was inferred from silence; coverage of the source was never verified. ");
            if (episode.ClosureReason == ClosureReason.ExpiredUnverified) sb.Append("This is an administrative expiry, not a confirmed recovery: the last known condition was ").Append(episode.ConditionState).Append(". ");
        }
        if (episode.ConditionState == ConditionState.Unknown) sb.Append("The condition is unknown because the source's coverage is degraded; the last known state was firing. ");
        return sb.ToString().TrimEnd();
    }
}
