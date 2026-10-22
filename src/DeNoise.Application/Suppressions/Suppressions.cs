using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Audit;
using DeNoise.Application.Heartbeats;
using DeNoise.Application.Integrations;
using DeNoise.Application.Mapping;
using DeNoise.Application.Notifications;
using DeNoise.Application.Ops;
using DeNoise.Application.Processing;
using DeNoise.Application.Routing;
using DeNoise.Application.Teams;
using DeNoise.Domain.Alerts;
using DeNoise.Domain.Audit;
using DeNoise.Domain.Common;
using DeNoise.Domain.Episodes;
using DeNoise.Domain.Integrations;
using DeNoise.Domain.Notifications;
using DeNoise.Domain.Ops;
using DeNoise.Domain.Policies;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeNoise.Application.Suppressions;

public interface ISuppressionRepository
{
    /// <summary>Tracked (the service mutates rows: start, end, cancel).</summary>
    Task<Suppression?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Suppression>> ListAsync(bool includeEnded, DateTimeOffset now, CancellationToken ct = default);
    /// <summary>Rows that suppress right now: not cancelled, started, not ended.</summary>
    Task<IReadOnlyList<Suppression>> ListActiveAsync(DateTimeOffset now, CancellationToken ct = default);
    void Add(Suppression suppression);
}

/// <summary>Create request: instants (<see cref="StartsAt"/>/<see cref="EndsAt"/>) or wall-clock times in <see cref="TimeZone"/> (<see cref="StartsLocal"/>/<see cref="EndsLocal"/>).</summary>
public sealed record CreateSuppression(string Kind, JsonNode Scope, string TimeZone, string Reason, DateTimeOffset? StartsAt = null, DateTimeOffset? EndsAt = null,
    DateTime? StartsLocal = null, DateTime? EndsLocal = null, bool AutoPauseHeartbeats = true, string? Name = null);

public sealed record SuppressionJobPayload(Guid SuppressionId, string Phase)
{
    public const string Start = "start";
    public const string End = "end";
}

/// <summary>Result of ending a window: what was re-evaluated and who got a summary (spec §16.3 "emits a current summary").</summary>
public sealed record SuppressionEndResult(int EpisodesReleased, int NotificationsMuted, IReadOnlyList<Guid> TeamsSummarised, int SummaryRows);

/// <summary>
/// Wall-clock ↔ instant conversion with an explicit IANA zone (spec §16.3: DST transitions are an acceptance case). A local time
/// that does not exist (spring forward) runs at the first valid instant after the gap; an ambiguous one (autumn) takes its first
/// occurrence. Either way the window keeps its wall-clock meaning: "01:30–03:30" is one hour on the spring night, three in autumn.
/// </summary>
public static class WallClock
{
    public static TimeZoneInfo Zone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new MappingValidationException([new MappingValidationError("$.timeZone", $"unknown IANA time zone '{id}'")]);
        }
    }

    public static DateTimeOffset ToInstant(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified))
        {
            // Inside the gap: the clocks jumped from the standard to the daylight offset; the first valid instant is the gap's end.
            var standard = zone.GetUtcOffset(unspecified.AddHours(-1));
            var daylight = zone.GetUtcOffset(unspecified.AddHours(1));
            var gap = daylight - standard;
            return new DateTimeOffset(unspecified.Add(gap), daylight);
        }
        if (zone.IsAmbiguousTime(unspecified))
        {
            var first = zone.GetAmbiguousTimeOffsets(unspecified).Max(); // the larger offset (daylight) is the earlier instant
            return new DateTimeOffset(unspecified, first);
        }
        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }

    public static DateTime ToLocal(DateTimeOffset instant, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(instant, zone).DateTime;
}

/// <summary>Human-readable predicates for the suppressions list (08 §3.7 "scope predicate rendered human-readably").</summary>
public static class PredicateText
{
    public static string Describe(Predicate predicate) => predicate switch
    {
        Predicate.Eq e => $"{e.Ref} is {Text(e.Value)}",
        Predicate.Neq n => $"{n.Ref} is not {Text(n.Value)}",
        Predicate.In i => $"{i.Ref} is one of {string.Join(", ", i.Values.Select(Text))}",
        Predicate.Nin n => $"{n.Ref} is none of {string.Join(", ", n.Values.Select(Text))}",
        Predicate.Regex r => $"{r.Ref} matches /{r.Pattern}/",
        Predicate.Exists x => $"{x.Ref} is set",
        Predicate.Gte g => $"{g.Ref} ≥ {Text(g.Value)}",
        Predicate.Lte l => $"{l.Ref} ≤ {Text(l.Value)}",
        Predicate.All a => a.Items.Count == 0 ? "everything" : string.Join(" and ", a.Items.Select(i => Wrap(i))),
        Predicate.Any a => a.Items.Count == 0 ? "nothing" : string.Join(" or ", a.Items.Select(i => Wrap(i))),
        Predicate.Not n => $"not ({Describe(n.Item)})",
        _ => predicate.ToString() ?? "?",
    };

    private static string Wrap(Predicate p) => p is Predicate.All or Predicate.Any ? $"({Describe(p)})" : Describe(p);
    private static string Text(JsonNode? value) => value is null ? "empty" : value is JsonValue v && v.TryGetValue<string>(out var s) ? $"'{s}'" : value.ToJsonString();
}

/// <summary>Predicate references (07 §5) over an episode without its opening event — the shape suppression windows evaluate against existing episodes.</summary>
public static class EpisodeRefs
{
    public static JsonNode? Resolve(string reference, Episode episode, Integration? integration)
    {
        switch (reference)
        {
            case CanonicalFields.Severity: return JsonValue.Create(episode.Severity.ToWire());
            case CanonicalFields.ResourceName: return Value(episode.ResourceName);
            case CanonicalFields.RuleName: return Value(episode.RuleName);
            case CanonicalFields.Environment: return Value(episode.Environment);
            case CanonicalFields.Service: return Value(episode.Service);
            case CanonicalFields.Summary: return Value(episode.Summary);
            case CanonicalFields.RunbookUrl: return Value(episode.RunbookUrl);
            case "integration.id": return JsonValue.Create(episode.IntegrationId.ToString());
            case "integration.type": return Value(integration?.Type);
            case "integration.name": return Value(integration?.Name);
            case "access_scope": return JsonValue.Create(episode.AccessScope);
            case "condition_state": return JsonValue.Create(episode.ConditionState);
            case "handling_state": return JsonValue.Create(episode.HandlingState);
            case "owning_team_id": return episode.OwningTeamId is { } t ? JsonValue.Create(t.ToString()) : null;
            case "is_actionable": return JsonValue.Create(episode.IsActionable);
            default: return null;
        }
    }

    /// <summary>A heartbeat only has a scope: maintenance windows pause it when their predicate is satisfied by the scope alone.</summary>
    public static Func<string, JsonNode?> ScopeOnly(string accessScope) => reference => reference == "access_scope" ? JsonValue.Create(accessScope) : null;

    private static JsonNode? Value(string? s) => s is null ? null : JsonValue.Create(s);
}

/// <summary>
/// Silences and maintenance windows (04 §7.5, spec §16.3). Suppression blocks delivery only: new episodes inside an active window
/// stage their notifications as <c>suppressed</c>; existing episodes are re-evaluated when a window starts and ends; at the end the
/// muted rows are coalesced (never replayed) and each team receives one <c>episode.summary_after_suppression</c> with what is
/// still actionable.
/// </summary>
public sealed class SuppressionService(
    ISuppressionRepository suppressions, IProcessingUnitOfWork processing, IJobQueue jobs, IIntegrationRepository integrations, ITeamRepository teams, IDestinationRepository destinations,
    IUnitOfWork uow, IAuditWriter audit, IOptions<NotificationOptions> options, TimeProvider time, ILogger<SuppressionService> logger)
{
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(31);

    public static Predicate ParseScope(JsonNode scope)
    {
        var errors = new List<MappingValidationError>();
        var predicate = PredicateParser.Parse(scope, "$.scope", errors, RoutingRefs.IsValid);
        if (errors.Count > 0 || predicate is null) throw new MappingValidationException(errors.Count > 0 ? errors : [new MappingValidationError("$.scope", "must be a predicate")]);
        return predicate;
    }

    public static bool Matches(Suppression suppression, Func<string, JsonNode?> resolve)
    {
        try
        {
            return PredicateEvaluator.Evaluate(ParseScope(JsonNode.Parse(suppression.Scope)!), resolve);
        }
        catch (Exception ex) when (ex is JsonException or MappingValidationException)
        {
            return false;
        }
    }

    public async Task<Suppression> CreateAsync(CreateSuppression request, Actor actor, CancellationToken ct = default)
    {
        var errors = new List<MappingValidationError>();
        if (!SuppressionKinds.All.Contains(request.Kind)) errors.Add(new MappingValidationError("$.kind", $"must be one of {string.Join(", ", SuppressionKinds.All)}"));
        if (string.IsNullOrWhiteSpace(request.Reason)) errors.Add(new MappingValidationError("$.reason", "a reason is required"));
        if (string.IsNullOrWhiteSpace(request.TimeZone)) errors.Add(new MappingValidationError("$.timeZone", "an IANA time zone is required (DST is explicit, spec §16.3)"));
        if (errors.Count > 0) throw new MappingValidationException(errors);
        var predicate = ParseScope(request.Scope);
        var zone = WallClock.Zone(request.TimeZone);
        var now = time.GetUtcNow();
        var starts = request.StartsAt ?? (request.StartsLocal is { } sl ? WallClock.ToInstant(sl, zone) : now);
        var ends = request.EndsAt ?? (request.EndsLocal is { } el ? WallClock.ToInstant(el, zone) : (DateTimeOffset?)null);
        if (ends is null) errors.Add(new MappingValidationError("$.endsAt", "an end is required (windows are time-bounded, spec §16.3)"));
        else if (ends <= starts) errors.Add(new MappingValidationError("$.endsAt", "must be after the start"));
        else if (ends <= now) errors.Add(new MappingValidationError("$.endsAt", "must be in the future"));
        else if (ends - starts > MaxWindow) errors.Add(new MappingValidationError("$.endsAt", $"a window is at most {MaxWindow.TotalDays:F0} days"));
        if (errors.Count > 0) throw new MappingValidationException(errors);

        var suppression = new Suppression
        {
            SuppressionId = Ids.New(time),
            Kind = request.Kind,
            Name = string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim(),
            Scope = request.Scope.ToJsonString(JsonDefaults.Stored),
            StartsAt = starts.ToUniversalTime(),
            EndsAt = ends!.Value.ToUniversalTime(),
            TimeZone = zone.Id,
            Reason = request.Reason.Trim(),
            AutoPauseHeartbeats = request.AutoPauseHeartbeats,
            CreatedBy = actor.Id,
            CreatedAt = now,
        };
        suppressions.Add(suppression);
        if (suppression.StartsAt > now) jobs.Enqueue(Job(suppression, SuppressionJobPayload.Start, suppression.StartsAt, now));
        jobs.Enqueue(Job(suppression, SuppressionJobPayload.End, suppression.EndsAt, now));
        audit.Record(Entry(actor, $"suppression.{suppression.Kind}.create", suppression, now, new { suppression.Kind, suppression.StartsAt, suppression.EndsAt, suppression.TimeZone, suppression.Reason, scope = PredicateText.Describe(predicate) }));
        await uow.CommitAsync(ct);
        if (suppression.StartsAt <= now) await StartAsync(suppression, ct);
        return suppression;
    }

    /// <summary>Cancel = end now: the summary goes out and the muted rows are coalesced, exactly as at a natural end.</summary>
    public async Task<SuppressionEndResult?> CancelAsync(Guid id, Actor actor, CancellationToken ct = default)
    {
        var suppression = await suppressions.GetAsync(id, ct) ?? throw new KeyNotFoundException($"Suppression {id} not found.");
        var now = time.GetUtcNow();
        if (suppression.HasEnded(now)) return null;
        suppression.CancelledAt = now;
        suppression.CancelledBy = actor.Id;
        suppression.EndsAt = now;
        audit.Record(Entry(actor, $"suppression.{suppression.Kind}.cancel", suppression, now, new { endedAt = now }));
        await uow.CommitAsync(ct);
        return await EndAsync(suppression, "cancelled", ct);
    }

    /// <summary>At open: the episode is muted until the last matching active window ends (04 §2.3). Returns true when suppressed.</summary>
    public async Task<bool> ApplyToNewEpisodeAsync(Episode episode, NormalisedEvent evt, Integration integration, IProcessingSession session, DateTimeOffset now, CancellationToken ct)
    {
        var active = await suppressions.ListActiveAsync(now, ct);
        if (active.Count == 0) return false;
        Func<string, JsonNode?> resolve = reference => RoutingEngine.ResolveRef(reference, evt, episode, integration);
        var matching = active.Where(s => Matches(s, resolve)).OrderByDescending(s => s.EndsAt).ToList();
        if (matching.Count == 0) return false;
        var last = matching[0];
        episode.SuppressedUntil = last.EndsAt;
        episode.SuppressionSource = last.Kind;
        session.AddEpisodeEvent(Timeline(episode, now, new { suppressionId = last.SuppressionId, kind = last.Kind, reason = last.Reason, until = last.EndsAt, detail = "opened inside a suppression window: notifications are staged muted (spec §16.3)" }));
        return true;
    }

    /// <summary>Window start: existing open episodes in scope are muted and their pending notifications held back.</summary>
    public async Task<int> StartAsync(Suppression suppression, CancellationToken ct = default)
    {
        var affected = await processing.RunAsync(async session =>
        {
            var now = time.GetUtcNow();
            var count = 0;
            foreach (var episode in await session.ListOpenEpisodesForUpdateAsync(ct))
            {
                var integration = await integrations.GetCurrentAsync(episode.IntegrationId, ct);
                if (!Matches(suppression, r => EpisodeRefs.Resolve(r, episode, integration))) continue;
                if (episode.SuppressedUntil is null || episode.SuppressedUntil < suppression.EndsAt)
                {
                    episode.SuppressedUntil = suppression.EndsAt;
                    episode.SuppressionSource = suppression.Kind;
                }
                episode.Touch(now);
                var held = await session.SuppressPendingOutboxAsync(episode.EpisodeId, ct);
                session.AddEpisodeEvent(Timeline(episode, now, new { suppressionId = suppression.SuppressionId, kind = suppression.Kind, reason = suppression.Reason, until = suppression.EndsAt, heldNotifications = held }));
                count++;
            }
            return count;
        }, ct);
        var tracked = await suppressions.GetAsync(suppression.SuppressionId, ct);
        if (tracked is not null && tracked.StartedAt is null)
        {
            tracked.StartedAt = time.GetUtcNow();
            await uow.CommitAsync(ct);
        }
        logger.LogInformation("Suppression {SuppressionId} ({Kind}) started: {Count} open episode(s) muted", suppression.SuppressionId, suppression.Kind, affected);
        return affected;
    }

    /// <summary>
    /// Window end (spec §16.3): open episodes are re-evaluated — released unless another window still covers them — their muted
    /// rows are coalesced, and every team with actionable episodes in the scope gets one current summary. Nothing is replayed.
    /// </summary>
    public async Task<SuppressionEndResult> EndAsync(Suppression suppression, string trigger, CancellationToken ct = default)
    {
        var result = await processing.RunAsync(async session =>
        {
            var now = time.GetUtcNow();
            var others = (await suppressions.ListActiveAsync(now, ct)).Where(s => s.SuppressionId != suppression.SuppressionId).ToList();
            var released = 0;
            var muted = 0;
            var byTeam = new Dictionary<Guid, List<Episode>>();
            foreach (var episode in await session.ListOpenEpisodesForUpdateAsync(ct))
            {
                var integration = await integrations.GetCurrentAsync(episode.IntegrationId, ct);
                Func<string, JsonNode?> resolve = r => EpisodeRefs.Resolve(r, episode, integration);
                var inScope = Matches(suppression, resolve);
                if (episode.SuppressedUntil is not null)
                {
                    var still = others.Where(o => Matches(o, resolve)).OrderByDescending(o => o.EndsAt).FirstOrDefault();
                    if (episode.SuppressionSource == "silence" && !inScope && episode.SuppressedUntil > now)
                    {
                        // A per-episode silence (episode action) is not this window's: leave it alone.
                    }
                    else if (still is not null)
                    {
                        episode.SuppressedUntil = still.EndsAt;
                        episode.SuppressionSource = still.Kind;
                    }
                    else if (inScope || episode.SuppressedUntil <= now)
                    {
                        episode.SuppressedUntil = null;
                        episode.SuppressionSource = null;
                        episode.Touch(now);
                        muted += await session.CoalesceSuppressedOutboxAsync(episode.EpisodeId, $"muted by {suppression.Kind} '{suppression.Name ?? suppression.Reason}'; not replayed (spec §16.3)", ct);
                        released++;
                        session.AddEpisodeEvent(Timeline(episode, now, new { suppressionId = suppression.SuppressionId, kind = suppression.Kind, ended = trigger, detail = "suppression ended; delivery resumes with a summary, muted notifications are not replayed" }));
                    }
                }
                if (inScope && episode.IsOpen && episode.IsActionable && episode.OwningTeamId is { } teamId)
                {
                    if (!byTeam.TryGetValue(teamId, out var list)) byTeam[teamId] = list = [];
                    list.Add(episode);
                }
            }

            var summaryRows = 0;
            var summarised = new List<Guid>();
            foreach (var (teamId, episodes) in byTeam)
            {
                var team = await teams.GetAsync(teamId, ct);
                var targets = (await destinations.ListForTeamAsync(teamId, ct)).Where(d => d.Active && d.Subscribes(NotificationTypes.EpisodeSummaryAfterSuppression)).DistinctBy(d => d.DestinationId).ToList();
                if (team is null || targets.Count == 0) continue;
                var model = SummaryModel(suppression, team, episodes, now);
                var payload = model.ToJsonString(JsonDefaults.Stored);
                foreach (var destination in targets)
                {
                    session.AddOutbox(new OutboxMessage { OutboxId = Ids.New(time), Type = NotificationTypes.EpisodeSummaryAfterSuppression, DestinationId = destination.DestinationId, Payload = payload, NotBefore = now, CreatedAt = now });
                    summaryRows++;
                }
                summarised.Add(teamId);
                foreach (var episode in episodes)
                {
                    session.AddEpisodeEvent(new EpisodeEvent { Id = Ids.New(time), EpisodeId = episode.EpisodeId, At = now, Kind = EpisodeEventKind.SuppressionSummary, Detail = JsonSerializer.Serialize(new { suppressionId = suppression.SuppressionId, teamId, destinations = targets.Count }, JsonDefaults.Stored) });
                }
            }
            return new SuppressionEndResult(released, muted, summarised, summaryRows);
        }, ct);

        var tracked = await suppressions.GetAsync(suppression.SuppressionId, ct);
        if (tracked is not null && tracked.SummarySentAt is null)
        {
            tracked.SummarySentAt = time.GetUtcNow();
            await uow.CommitAsync(ct);
        }
        logger.LogInformation("Suppression {SuppressionId} ended ({Trigger}): {Released} episode(s) released, {Muted} muted notification(s) coalesced, {Teams} team summary(ies)", suppression.SuppressionId, trigger, result.EpisodesReleased, result.NotificationsMuted, result.TeamsSummarised.Count);
        return result;
    }

    private JsonObject SummaryModel(Suppression suppression, Domain.Teams.Team team, IReadOnlyList<Episode> episodes, DateTimeOffset now)
    {
        var items = new JsonArray();
        foreach (var e in episodes.OrderByDescending(e => e.Severity.Rank()).ThenByDescending(e => e.LastSeen))
        {
            items.Add(new JsonObject
            {
                ["id"] = e.EpisodeId.ToString(),
                ["severity"] = e.Severity.ToWire(),
                ["summary"] = e.Summary,
                ["service"] = e.Service,
                ["environment"] = e.Environment,
                ["resource"] = e.ResourceName,
                ["handlingState"] = e.HandlingState,
                ["conditionState"] = e.ConditionState,
                ["lastSeen"] = e.LastSeen.ToString("O"),
                ["url"] = $"{options.Value.PublicBaseUrl.TrimEnd('/')}/episodes/{e.EpisodeId}",
            });
        }
        var model = NotificationModel.ForHub(NotificationTypes.EpisodeSummaryAfterSuppression, new
        {
            suppression = new { id = suppression.SuppressionId, kind = suppression.Kind, name = suppression.Name, reason = suppression.Reason, startsAt = suppression.StartsAt, endsAt = suppression.EndsAt, timeZone = suppression.TimeZone },
            team = new { id = team.TeamId, name = team.Name },
            count = episodes.Count,
        });
        model["episodes"] = items;
        model["team"] = new JsonObject { ["id"] = team.TeamId.ToString(), ["name"] = team.Name };
        model["summary"] = $"{episodes.Count} episode(s) still need attention after {suppression.Kind} '{suppression.Name ?? suppression.Reason}' ended";
        model["occurredAt"] = now.ToString("O");
        return model;
    }

    private Job Job(Suppression s, string phase, DateTimeOffset at, DateTimeOffset now) => new()
    {
        JobId = Ids.New(time),
        Kind = JobKinds.SuppressionEnd,
        NotBefore = at,
        Payload = JsonSerializer.Serialize(new SuppressionJobPayload(s.SuppressionId, phase), JsonDefaults.Stored),
        CreatedAt = now,
        UpdatedAt = now,
    };

    private EpisodeEvent Timeline(Episode episode, DateTimeOffset now, object detail) => new()
    {
        Id = Ids.New(time),
        EpisodeId = episode.EpisodeId,
        At = now,
        Kind = EpisodeEventKind.Suppress,
        Detail = JsonSerializer.Serialize(detail, JsonDefaults.Stored),
    };

    private AuditEntry Entry(Actor actor, string action, Suppression s, DateTimeOffset now, object after) => new()
    {
        Id = Ids.New(time),
        At = now,
        ActorType = actor.Type,
        ActorId = actor.Id,
        ActorDisplay = actor.Display,
        Action = action,
        TargetType = "suppression",
        TargetId = s.SuppressionId.ToString(),
        After = JsonSerializer.Serialize(after, JsonDefaults.Stored),
        Reason = s.Reason,
        CorrelationId = actor.CorrelationId,
        RequestIp = actor.Ip,
    };
}

/// <summary><c>suppression_end</c> jobs carry a phase: <c>start</c> mutes existing episodes when a future window begins, <c>end</c> releases and summarises.</summary>
public sealed class SuppressionJobHandler(ISuppressionRepository suppressions, SuppressionService service, IJobQueue jobs, IUnitOfWork uow, TimeProvider time, ILogger<SuppressionJobHandler> logger) : IJobHandler
{
    public string Kind => JobKinds.SuppressionEnd;

    public async Task HandleAsync(Job job, JobContext context, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<SuppressionJobPayload>(job.Payload, JsonDefaults.Stored) ?? throw new InvalidOperationException("suppression job without payload");
        var suppression = await suppressions.GetAsync(payload.SuppressionId, ct);
        if (suppression is null) return;
        var now = time.GetUtcNow();
        if (payload.Phase == SuppressionJobPayload.Start)
        {
            if (suppression.CancelledAt is null && suppression.StartedAt is null && suppression.EndsAt > now) await service.StartAsync(suppression, ct);
            return;
        }
        if (suppression.SummarySentAt is not null) return; // cancelled earlier: the cancel already ended it
        if (suppression.EndsAt > now)
        {
            // The end moved later (edited window): come back then.
            jobs.Enqueue(new Job { JobId = Ids.New(time), Kind = JobKinds.SuppressionEnd, NotBefore = suppression.EndsAt, Payload = job.Payload, CreatedAt = now, UpdatedAt = now });
            await uow.CommitAsync(ct);
            logger.LogInformation("Suppression {SuppressionId} ends later than scheduled; end re-queued for {EndsAt}", suppression.SuppressionId, suppression.EndsAt);
            return;
        }
        await service.EndAsync(suppression, "window ended", ct);
    }
}

/// <summary>Heartbeat auto-pause (spec §22 "Maintenance window covers a heartbeat's scope"): an active maintenance window whose scope predicate is satisfied by the heartbeat's access scope alone.</summary>
public sealed class SuppressionMaintenanceWindows(ISuppressionRepository suppressions) : IMaintenanceWindows
{
    public async Task<bool> CoversAsync(string accessScope, DateTimeOffset now, CancellationToken ct = default)
    {
        var active = await suppressions.ListActiveAsync(now, ct);
        var resolve = EpisodeRefs.ScopeOnly(accessScope);
        return active.Any(s => s.Kind == SuppressionKinds.Maintenance && s.AutoPauseHeartbeats && SuppressionService.Matches(s, resolve));
    }
}

/// <summary>Formats a window for lists: "Sat 2026-03-28 01:30 → 03:30 Europe/Warsaw".</summary>
public static class SuppressionText
{
    public static string Window(Suppression s)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(s.TimeZone);
        var starts = WallClock.ToLocal(s.StartsAt, zone);
        var ends = WallClock.ToLocal(s.EndsAt, zone);
        var sb = new StringBuilder();
        sb.Append(starts.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(" → ");
        sb.Append(ends.Date == starts.Date ? ends.ToString("HH:mm", CultureInfo.InvariantCulture) : ends.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        sb.Append(' ').Append(s.TimeZone);
        return sb.ToString();
    }
}
