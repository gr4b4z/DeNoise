using System.Text.Json;
using System.Text.Json.Nodes;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Integrations;
using DeNoise.Application.Mapping;
using DeNoise.Application.Notifications;
using DeNoise.Application.Ops;
using DeNoise.Application.Policies;
using DeNoise.Application.Processing;
using DeNoise.Application.Routing;
using DeNoise.Application.Teams;
using DeNoise.Application.Topology;
using DeNoise.Domain.Alerts;
using DeNoise.Domain.Common;
using DeNoise.Domain.Episodes;
using DeNoise.Domain.Integrations;
using DeNoise.Domain.Notifications;
using DeNoise.Domain.Ops;
using DeNoise.Domain.Policies;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeNoise.Application.Grouping;

/// <summary>What grouping decided for a newly opened episode.</summary>
public sealed record GroupingOutcome(AlertGroup? Group, bool IsFirstMember, bool Notify, string? RuleName)
{
    public static readonly GroupingOutcome None = new(null, true, true, null);
}

public sealed record GroupWindowCloseJobPayload(Guid GroupId);
public sealed record GroupDigestJobPayload(Guid GroupId);

/// <summary>
/// Grouping at open time (04 §7.4, spec §16.3): the first matching rule of the active grouping policy computes the key from the
/// event; an open group for (rule, key) that still accepts members (window + late-join grace) adopts the episode and may extend its
/// window up to the hard maximum; otherwise a new group starts, adopting recent open episodes of the same key (retro look-back) and
/// recording which group it continues. Below <c>min_members</c> no group exists and the episode notifies at once. The group's
/// severity is the max active member; a new critical child stays visible and may notify on its own; a digest may roll up joins.
/// </summary>
public sealed class GroupingService(
    IPolicyRepository policies, IIntegrationRepository integrations, ITeamRepository teams, IDestinationRepository destinations,
    IOptions<NotificationOptions> options, TimeProvider time, ILogger<GroupingService> logger)
{
    public const int CandidateLimit = 1000;

    public async Task<GroupingPolicyDocument?> ActiveAsync(CancellationToken ct)
    {
        var version = await policies.GetActiveAsync(PolicyKinds.Grouping, WellKnownPolicies.Grouping, ct);
        if (version is null) return null;
        try
        {
            return GroupingPolicyDocument.Parse(JsonNode.Parse(version.Body)!, version.Version);
        }
        catch (MappingValidationException ex)
        {
            logger.LogError(ex, "Active grouping policy v{Version} does not parse; grouping disabled", version.Version);
            return null;
        }
    }

    /// <summary>Canonical JSON of the key fields in field order; null when a key field has no value (an ambiguous key never groups).</summary>
    public static string? KeyJson(IReadOnlyList<string> key, Func<string, JsonNode?> resolve)
    {
        var obj = new JsonObject();
        foreach (var field in key.OrderBy(k => k, StringComparer.Ordinal))
        {
            var value = resolve(field);
            if (value is null) return null;
            obj[field] = value.DeepClone();
        }
        return obj.ToJsonString(JsonDefaults.Stored);
    }

    public async Task<GroupingOutcome> ApplyAsync(Episode episode, NormalisedEvent evt, Integration integration, IProcessingSession session, DateTimeOffset now, CancellationToken ct)
    {
        var policy = await ActiveAsync(ct);
        if (policy is null) return GroupingOutcome.None;
        JsonNode? Resolve(string reference) => RoutingEngine.ResolveRef(reference, evt, episode, integration);
        var rule = policy.Rules.FirstOrDefault(r => PredicateEvaluator.Evaluate(r.Match, Resolve));
        if (rule is null) return GroupingOutcome.None;
        var keyJson = KeyJson(rule.Key, Resolve);
        if (keyJson is null) return GroupingOutcome.None;

        var group = await session.FindOpenGroupForUpdateAsync(rule.RuleId, keyJson, ct);
        Guid? continuedFrom = null;
        if (group is not null && group.AccessScope != episode.AccessScope)
        {
            return GroupingOutcome.None; // never join across scopes; the key is held by another scope's open group, so this episode notifies alone
        }
        if (group is not null && !group.Accepts(now, rule.Window.LateJoin))
        {
            // The predecessor reached its maximum or its grace passed before the close job ran: close it here so the successor can start.
            group.ClosedAt = now;
            continuedFrom = group.GroupId;
            group = null;
        }

        return group is not null
            ? await JoinAsync(group, rule, episode, evt, session, now, ct)
            : await MaterialiseAsync(rule, keyJson, continuedFrom, episode, evt, session, now, ct);
    }

    private async Task<GroupingOutcome> JoinAsync(AlertGroup group, GroupingRule rule, Episode episode, NormalisedEvent evt, IProcessingSession session, DateTimeOffset now, CancellationToken ct)
    {
        group.MemberCount++;
        group.LastMemberAt = now;
        if (episode.Severity.Rank() > SeverityExtensions.ParseWire(group.Severity).Rank()) group.Severity = episode.Severity.ToWire();
        var lateJoin = now > group.WindowEndsAt;
        var extended = group.Extend(rule.Window.ExtendBy);
        if (extended) await session.RescheduleGroupJobsAsync(group.GroupId, JobKinds.GroupWindowClose, group.WindowEndsAt, ct);
        episode.GroupId = group.GroupId;
        var notify = rule.Notify == GroupNotify.FirstAndNewCritical && episode.Severity == Severity.Critical;
        Record(session, episode, now, evt.EventId, new
        {
            groupId = group.GroupId,
            rule = rule.DisplayName,
            member = group.MemberCount,
            first = false,
            notify,
            adopted = false,
            lateJoin,
            extended,
            extensionCount = group.ExtensionCount,
            windowEndsAt = group.WindowEndsAt,
            hardEndAt = group.WindowHardEndAt,
            groupSeverity = group.Severity,
        });
        return new GroupingOutcome(group, false, notify, rule.Name);
    }

    /// <summary>
    /// A new group for the key. Recent open episodes of the same rule and key (inside <c>retro.look_back</c>, or inside the initial
    /// window when <c>min_members</c> demands company) are adopted; exactly one opening notification survives for the key — anything
    /// already dispatched is never retracted, otherwise the earliest pending one — and the rest are coalesced (spec §16.3).
    /// </summary>
    private async Task<GroupingOutcome> MaterialiseAsync(GroupingRule rule, string keyJson, Guid? continuedFrom, Episode episode, NormalisedEvent evt, IProcessingSession session, DateTimeOffset now, CancellationToken ct)
    {
        var lookBack = rule.MinMembers > 1 && rule.Window.Initial > rule.LookBack ? rule.Window.Initial : rule.LookBack;
        var adoptees = lookBack > TimeSpan.Zero ? await FindAdopteesAsync(rule, keyJson, episode, session, now - lookBack, ct) : [];
        if (continuedFrom is null)
        {
            // The predecessor may have been closed by its timer already: a member arriving within one initial window of that close continues it.
            var predecessor = await session.FindLatestClosedGroupAsync(rule.RuleId, keyJson, ct);
            if (predecessor is { ClosedAt: { } closedAt } && now - closedAt <= rule.Window.Initial) continuedFrom = predecessor.GroupId;
        }
        if (1 + adoptees.Count < rule.MinMembers)
        {
            Record(session, episode, now, evt.EventId, new { rule = rule.DisplayName, groupId = (Guid?)null, first = true, notify = true, belowMinMembers = true, minMembers = rule.MinMembers, detail = "no group below min_members: notified immediately, grouping adds no latency (spec §16.3)" });
            return GroupingOutcome.None;
        }

        var group = new AlertGroup
        {
            GroupId = Ids.New(time),
            AccessScope = episode.AccessScope,
            RuleId = rule.RuleId,
            KeyValues = keyJson,
            OpenedAt = now,
            WindowEndsAt = now + rule.Window.Initial,
            WindowHardEndAt = now + rule.Window.Max,
            LastMemberAt = now,
            ContinuedFromGroupId = continuedFrom,
            Severity = episode.Severity.ToWire(),
            MemberCount = 1,
            DigestNextAt = rule.Digest is { } d ? now + d.Every : null,
        };
        session.AddGroup(group);
        session.AddJob(GroupJob(group, JobKinds.GroupWindowClose, group.WindowEndsAt, episode.IntegrationId, new GroupWindowCloseJobPayload(group.GroupId), now));
        if (group.DigestNextAt is { } digestAt) session.AddJob(GroupJob(group, JobKinds.GroupDigest, digestAt, episode.IntegrationId, new GroupDigestJobPayload(group.GroupId), now));
        episode.GroupId = group.GroupId;

        var survivor = false; // the opening notification that stands for the key
        foreach (var adoptee in adoptees.OrderBy(a => a.FirstSeen))
        {
            adoptee.GroupId = group.GroupId;
            adoptee.Touch(now);
            group.MemberCount++;
            group.AdoptedCount++;
            if (adoptee.Severity.Rank() > SeverityExtensions.ParseWire(group.Severity).Rank()) group.Severity = adoptee.Severity.ToWire();
            var statuses = await session.OutboxStatusesAsync(adoptee.EpisodeId, NotificationTypes.EpisodeOpened, ct);
            var dispatched = statuses.Any(s => s is OutboxStatus.Sent or OutboxStatus.Reserved or OutboxStatus.Failed);
            string kept;
            if (dispatched)
            {
                survivor = true;
                kept = "already dispatched; never retracted";
            }
            else if (!survivor && statuses.Contains(OutboxStatus.Pending))
            {
                survivor = true;
                kept = "pending; stands as the group's opening notification";
            }
            else
            {
                var coalesced = await session.CoalescePendingOutboxAsync(adoptee.EpisodeId, NotificationTypes.EpisodeOpened, $"adopted into group {group.GroupId}; the key already has an opening notification (spec §16.3)", ct);
                kept = coalesced > 0 ? $"{coalesced} undispatched notification(s) cancelled" : "no notification pending";
            }
            Record(session, adoptee, now, evt.EventId, new { groupId = group.GroupId, rule = rule.DisplayName, member = group.MemberCount, first = false, notify = false, adopted = true, notification = kept, windowEndsAt = group.WindowEndsAt, hardEndAt = group.WindowHardEndAt });
        }

        var first = !survivor;
        var notify = first || (rule.Notify == GroupNotify.FirstAndNewCritical && episode.Severity == Severity.Critical);
        Record(session, episode, now, evt.EventId, new
        {
            groupId = group.GroupId,
            rule = rule.DisplayName,
            member = 1,
            first,
            notify,
            adopted = false,
            adoptedMembers = adoptees.Count,
            continuedFrom,
            windowEndsAt = group.WindowEndsAt,
            hardEndAt = group.WindowHardEndAt,
            lateJoin = rule.Window.LateJoin.ToString(),
            digestNextAt = group.DigestNextAt,
            groupSeverity = group.Severity,
        });
        return new GroupingOutcome(group, first, notify, rule.Name);
    }

    private async Task<List<Episode>> FindAdopteesAsync(GroupingRule rule, string keyJson, Episode episode, IProcessingSession session, DateTimeOffset since, CancellationToken ct)
    {
        var candidates = await session.ListOpenEpisodesOpenedSinceAsync(episode.AccessScope, since, CandidateLimit, forUpdate: true, ct);
        var events = await session.LatestNormalisedEventsAsync(candidates.Select(c => c.EpisodeId).ToList(), ct);
        var adoptees = new List<Episode>();
        foreach (var candidate in candidates)
        {
            if (candidate.EpisodeId == episode.EpisodeId || candidate.GroupId is not null || !candidate.IsActionable) continue;
            var integration = await integrations.GetCurrentAsync(candidate.IntegrationId, ct);
            var resolve = TopologyMatcher.Resolver(candidate, events.GetValueOrDefault(candidate.EpisodeId), integration);
            if (!PredicateEvaluator.Evaluate(rule.Match, resolve)) continue;
            if (KeyJson(rule.Key, resolve) != keyJson) continue;
            adoptees.Add(candidate);
        }
        return adoptees;
    }

    /// <summary>A member's severity rose: the group carries the max active member (04 §7.4).</summary>
    public async Task OnSeverityRaisedAsync(Episode episode, IProcessingSession session, CancellationToken ct)
    {
        if (episode.GroupId is not { } groupId) return;
        var group = await session.FindGroupForUpdateAsync(groupId, ct);
        if (group is null || !group.IsOpen) return;
        if (episode.Severity.Rank() > SeverityExtensions.ParseWire(group.Severity).Rank()) group.Severity = episode.Severity.ToWire();
    }

    /// <summary><c>group_digest</c> body: one roll-up per team of the members that joined since the previous digest, capped at <c>max_items</c> with an "and N more" count.</summary>
    public JsonObject DigestModel(AlertGroup group, GroupingRule rule, Domain.Teams.Team team, IReadOnlyList<Episode> joined, DateTimeOffset since, DateTimeOffset now)
    {
        var items = new JsonArray();
        var shown = joined.OrderByDescending(e => e.Severity.Rank()).ThenByDescending(e => e.LastSeen).Take(rule.Digest?.MaxItems ?? GroupingPolicyDocument.DefaultDigestMaxItems).ToList();
        foreach (var e in shown)
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
        var model = NotificationModel.ForHub(NotificationTypes.EpisodeGroupDigest, new
        {
            group = new { id = group.GroupId, rule = rule.DisplayName, key = JsonNode.Parse(group.KeyValues), openedAt = group.OpenedAt, windowEndsAt = group.WindowEndsAt, hardEndAt = group.WindowHardEndAt, extensionCount = group.ExtensionCount, memberCount = group.MemberCount, severity = group.Severity, open = group.IsOpen },
            since,
            team = new { id = team.TeamId, name = team.Name },
            count = joined.Count,
            shown = shown.Count,
            more = joined.Count - shown.Count,
        });
        model["group"] = new JsonObject
        {
            ["id"] = group.GroupId.ToString(),
            ["rule"] = rule.DisplayName,
            ["key"] = JsonNode.Parse(group.KeyValues),
            ["memberCount"] = group.MemberCount,
            ["severity"] = group.Severity,
            ["extensionCount"] = group.ExtensionCount,
            ["windowEndsAt"] = group.WindowEndsAt.ToString("O"),
            ["open"] = group.IsOpen,
        };
        model["episodes"] = items;
        model["more"] = joined.Count - shown.Count;
        model["team"] = new JsonObject { ["id"] = team.TeamId.ToString(), ["name"] = team.Name };
        model["summary"] = $"{joined.Count} episode(s) joined group '{rule.DisplayName}' since {since:HH:mm} UTC" + (joined.Count > shown.Count ? $" ({shown.Count} shown)" : string.Empty);
        model["occurredAt"] = now.ToString("O");
        return model;
    }

    private Job GroupJob(AlertGroup group, string kind, DateTimeOffset at, Guid integrationId, object payload, DateTimeOffset now) => new()
    {
        JobId = Ids.New(time),
        Kind = kind,
        NotBefore = at,
        IntegrationId = integrationId,
        Payload = JsonSerializer.Serialize(payload, JsonDefaults.Stored),
        CreatedAt = now,
        UpdatedAt = now,
    };

    private void Record(IProcessingSession session, Episode episode, DateTimeOffset now, Guid? eventId, object detail)
        => session.AddEpisodeEvent(new EpisodeEvent { Id = Ids.New(time), EpisodeId = episode.EpisodeId, At = now, Kind = EpisodeEventKind.Grouped, EventId = eventId, Detail = JsonSerializer.Serialize(detail, JsonDefaults.Stored) });

    internal async Task<IReadOnlyList<Destination>> TeamTargetsAsync(Guid teamId, string type, CancellationToken ct)
        => (await destinations.ListForTeamAsync(teamId, ct)).Where(d => d.Active && d.Subscribes(type)).DistinctBy(d => d.DestinationId).ToList();

    internal Task<Domain.Teams.Team?> TeamAsync(Guid teamId, CancellationToken ct) => teams.GetAsync(teamId, ct);

    internal Task<Integration?> IntegrationAsync(Guid integrationId, CancellationToken ct) => integrations.GetCurrentAsync(integrationId, ct);
}

/// <summary>
/// <c>group_window_close</c>: the window ended; the group stops adopting members (04 §7.4). Members are untouched. When the window
/// was extended after the job was queued, the job re-arms itself to the new end instead of closing early.
/// </summary>
public sealed class GroupWindowCloseJobHandler(IProcessingUnitOfWork uow, TimeProvider time) : IJobHandler
{
    public string Kind => JobKinds.GroupWindowClose;

    public async Task HandleAsync(Job job, JobContext context, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<GroupWindowCloseJobPayload>(job.Payload, JsonDefaults.Stored) ?? throw new InvalidOperationException("group_window_close without payload");
        await uow.RunAsync<object?>(async session =>
        {
            var group = await session.FindGroupForUpdateAsync(payload.GroupId, ct);
            if (group is not { IsOpen: true }) return null;
            var now = time.GetUtcNow();
            if (group.WindowEndsAt > now)
            {
                await session.RescheduleJobAsync(job.JobId, group.WindowEndsAt, 0, group.WindowEndsAt, job.Payload, ct);
                context.Retained = true;
                return null;
            }
            group.ClosedAt = now;
            return null;
        }, ct);
    }
}

/// <summary>
/// <c>group_digest</c> (spec §16.3 "Digest"): one <c>episode.group_digest</c> per team with the members that joined since the previous
/// digest, capped at <c>max_items</c>; re-armed every <c>every</c> while the group is open, one last time after it closed.
/// </summary>
public sealed class GroupDigestJobHandler(IProcessingUnitOfWork uow, GroupingService grouping, TimeProvider time, ILogger<GroupDigestJobHandler> logger) : IJobHandler
{
    public string Kind => JobKinds.GroupDigest;

    public async Task HandleAsync(Job job, JobContext context, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<GroupDigestJobPayload>(job.Payload, JsonDefaults.Stored) ?? throw new InvalidOperationException("group_digest without payload");
        var staged = await uow.RunAsync(async session =>
        {
            var group = await session.FindGroupForUpdateAsync(payload.GroupId, ct);
            if (group is null) return 0;
            var policy = await grouping.ActiveAsync(ct);
            var rule = policy?.Rules.FirstOrDefault(r => r.RuleId == group.RuleId);
            if (rule?.Digest is not { } digest)
            {
                group.DigestNextAt = null; // the rule lost its digest: stop quietly
                return 0;
            }
            var now = time.GetUtcNow();
            var since = group.DigestLastAt ?? group.OpenedAt;
            var joined = (await session.ListGroupMembersJoinedSinceAsync(group.GroupId, since, inclusive: group.DigestLastAt is null, ct)).ToList();
            var rows = 0;
            if (joined.Count > 0)
            {
                var visible = new List<Episode>();
                foreach (var member in joined)
                {
                    var integration = await grouping.IntegrationAsync(member.IntegrationId, ct);
                    if (integration is { Shadow: false }) visible.Add(member); // shadow integrations never reach the outbox
                }
                foreach (var byTeam in visible.Where(m => m.OwningTeamId is not null).GroupBy(m => m.OwningTeamId!.Value))
                {
                    var team = await grouping.TeamAsync(byTeam.Key, ct);
                    var targets = await grouping.TeamTargetsAsync(byTeam.Key, NotificationTypes.EpisodeGroupDigest, ct);
                    if (team is null || targets.Count == 0) continue;
                    var members = byTeam.ToList();
                    var body = grouping.DigestModel(group, rule, team, members, since, now).ToJsonString(JsonDefaults.Stored);
                    foreach (var destination in targets)
                    {
                        session.AddOutbox(new OutboxMessage { OutboxId = Ids.New(time), Type = NotificationTypes.EpisodeGroupDigest, DestinationId = destination.DestinationId, Payload = body, NotBefore = now, CreatedAt = now });
                        rows++;
                    }
                    foreach (var member in members)
                    {
                        session.AddEpisodeEvent(new EpisodeEvent { Id = Ids.New(time), EpisodeId = member.EpisodeId, At = now, Kind = EpisodeEventKind.Notify, Detail = JsonSerializer.Serialize(new { type = NotificationTypes.EpisodeGroupDigest, groupId = group.GroupId, teamId = byTeam.Key, destinations = targets.Count, listed = members.Count }, JsonDefaults.Stored) });
                    }
                }
                group.DigestLastAt = now;
            }
            if (group.IsOpen)
            {
                group.DigestNextAt = now + digest.Every;
                await session.RescheduleJobAsync(job.JobId, group.DigestNextAt.Value, 0, group.DigestNextAt.Value, job.Payload, ct);
                context.Retained = true;
            }
            else
            {
                group.DigestNextAt = null;
            }
            return rows;
        }, ct);
        if (staged > 0) logger.LogInformation("Group {GroupId}: digest staged to {Rows} destination(s)", payload.GroupId, staged);
    }
}
