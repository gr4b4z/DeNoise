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
using DeNoise.Application.Suppressions;
using DeNoise.Application.Teams;
using DeNoise.Domain.Alerts;
using DeNoise.Domain.Common;
using DeNoise.Domain.Episodes;
using DeNoise.Domain.Integrations;
using DeNoise.Domain.Notifications;
using DeNoise.Domain.Ops;
using DeNoise.Domain.Policies;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeNoise.Application.Topology;

/// <summary>What the topology policy decided for a newly opened (or reopened) episode on its child side.</summary>
public sealed record TopologyOutcome(TopologyRule? Rule, Episode? Parent, bool Muted, AlertGroup? Group, bool GroupNotify)
{
    public static readonly TopologyOutcome None = new(null, null, false, null, true);
    public bool Attached => Parent is not null;
}

public sealed record DependencyReleaseJobPayload(Guid ParentEpisodeId, DateTimeOffset WindowEndsAt);

public sealed record DependencyReleaseResult(int Released, int NotificationsCoalesced, IReadOnlyList<Guid> TeamsSummarised, int SummaryRows);

/// <summary>Predicate and key evaluation shared by the processing path and the impact preview: a candidate is judged by its latest event when one exists, otherwise by the episode alone.</summary>
public static class TopologyMatcher
{
    public static Func<string, JsonNode?> Resolver(Episode episode, NormalisedEvent? evt, Integration? integration)
        => evt is not null && integration is not null
            ? reference => RoutingEngine.ResolveRef(reference, evt, episode, integration)
            : reference => EpisodeRefs.Resolve(reference, episode, integration);

    /// <summary>Key values in key order; null when any key field has no value (an ambiguous key never relates episodes).</summary>
    public static IReadOnlyList<string>? KeyValues(IReadOnlyList<string> key, Func<string, JsonNode?> resolve)
    {
        var values = new List<string>(key.Count);
        foreach (var field in key)
        {
            var value = resolve(field);
            if (ValueCoercion.IsEmpty(value)) return null;
            values.Add(ValueCoercion.AsDisplayString(value!));
        }
        return values;
    }

    public static bool KeysEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
        => a.Count == b.Count && a.Zip(b).All(p => string.Equals(p.First, p.Second, StringComparison.Ordinal));

    /// <summary>Does <paramref name="candidate"/> qualify as the parent of a child with <paramref name="childKey"/> under <paramref name="rule"/>?</summary>
    public static bool IsParentOf(TopologyRule rule, Episode candidate, Func<string, JsonNode?> resolveCandidate, IReadOnlyList<string> childKey)
    {
        if (!PredicateEvaluator.Evaluate(rule.Parent.Match, resolveCandidate)) return false;
        var parentKey = KeyValues(rule.Parent.Key, resolveCandidate);
        if (parentKey is null) return false;
        return rule.Child.Key.Count == 0 ? rule.Parent.IsStatic : KeysEqual(parentKey, childKey);
    }

    /// <summary>Does <paramref name="candidate"/> qualify as a child of a parent with <paramref name="parentKey"/> under <paramref name="rule"/>?</summary>
    public static bool IsChildOf(TopologyRule rule, Episode candidate, Func<string, JsonNode?> resolveCandidate, IReadOnlyList<string> parentKey)
    {
        if (!PredicateEvaluator.Evaluate(rule.Child.Match, resolveCandidate)) return false;
        if (rule.Child.Key.Count == 0) return rule.Parent.IsStatic;
        var childKey = KeyValues(rule.Child.Key, resolveCandidate);
        return childKey is not null && KeysEqual(parentKey, childKey);
    }
}

/// <summary>
/// Dependency suppression (spec §16.5): a child episode is attributed to an open parent found by field equality or a static list;
/// only its notification is affected — it stays ingested, owned, routed, actionable and visible. Runs inside the processing
/// transaction after suppression windows and before grouping; releases dependents when the parent closes or the window ends.
/// </summary>
public sealed class TopologyService(
    IPolicyRepository policies, IIntegrationRepository integrations, ITeamRepository teams, IDestinationRepository destinations,
    IOptions<NotificationOptions> options, TimeProvider time, ILogger<TopologyService> logger)
{
    /// <summary>How many recently opened episodes a parent or child search inspects; storms beyond this are matched against the newest ones.</summary>
    public const int CandidateLimit = 1000;

    public async Task<TopologyPolicyDocument?> ActiveAsync(CancellationToken ct)
    {
        var version = await policies.GetActiveAsync(PolicyKinds.Topology, WellKnownPolicies.Topology, ct);
        if (version is null) return null;
        try
        {
            return TopologyPolicyDocument.Parse(JsonNode.Parse(version.Body)!, version.Version);
        }
        catch (MappingValidationException ex)
        {
            logger.LogError(ex, "Active topology policy v{Version} does not parse; dependency suppression disabled", version.Version);
            return null;
        }
    }

    /// <summary>Child side at open/reopen: the first rule whose child side matches and whose parent is open inside the window decides.</summary>
    public async Task<TopologyOutcome> ApplyAsync(Episode episode, NormalisedEvent evt, Integration integration, IProcessingSession session, DateTimeOffset now, CancellationToken ct)
    {
        if (!episode.IsActionable || episode.IsDependent) return TopologyOutcome.None;
        var policy = await ActiveAsync(ct);
        if (policy is null || policy.Rules.Count == 0) return TopologyOutcome.None;
        var resolve = TopologyMatcher.Resolver(episode, evt, integration);
        foreach (var rule in policy.Rules)
        {
            if (!PredicateEvaluator.Evaluate(rule.Child.Match, resolve)) continue;
            var childKey = TopologyMatcher.KeyValues(rule.Child.Key, resolve);
            if (childKey is null) continue;
            var parent = await FindParentAsync(rule, episode, childKey, session, now, ct);
            if (parent is null) continue; // no parent matches: the child notifies normally (spec §22)
            return await AttachAsync(episode, parent, rule, session, now, evt.EventId, adopted: false, ct);
        }
        return TopologyOutcome.None;
    }

    /// <summary>
    /// Parent side at open: children that fired first (inside <c>look_back</c>) are adopted on the same terms; their undispatched
    /// opening notifications are muted, anything already sent is never retracted (spec §16.5).
    /// </summary>
    public async Task<int> AdoptAsync(Episode parent, NormalisedEvent evt, Integration integration, IProcessingSession session, DateTimeOffset now, CancellationToken ct)
    {
        if (!parent.IsActionable) return 0;
        var policy = await ActiveAsync(ct);
        if (policy is null) return 0;
        var resolveParent = TopologyMatcher.Resolver(parent, evt, integration);
        var adopted = 0;
        foreach (var rule in policy.Rules.Where(r => r.LookBack > TimeSpan.Zero))
        {
            if (!PredicateEvaluator.Evaluate(rule.Parent.Match, resolveParent)) continue;
            var parentKey = TopologyMatcher.KeyValues(rule.Parent.Key, resolveParent);
            if (parentKey is null) continue;
            var candidates = await session.ListOpenEpisodesOpenedSinceAsync(parent.AccessScope, now - rule.LookBack, CandidateLimit, forUpdate: true, ct);
            var events = await session.LatestNormalisedEventsAsync(candidates.Select(c => c.EpisodeId).ToList(), ct);
            foreach (var candidate in candidates)
            {
                if (candidate.EpisodeId == parent.EpisodeId || !candidate.IsActionable || candidate.IsDependent) continue;
                var candidateIntegration = await integrations.GetCurrentAsync(candidate.IntegrationId, ct);
                var resolve = TopologyMatcher.Resolver(candidate, events.GetValueOrDefault(candidate.EpisodeId), candidateIntegration);
                if (!TopologyMatcher.IsChildOf(rule, candidate, resolve, parentKey)) continue;
                var outcome = await AttachAsync(candidate, parent, rule, session, now, evt.EventId, adopted: true, ct);
                if (outcome.Muted)
                {
                    var held = await session.SuppressPendingOutboxAsync(candidate.EpisodeId, ct);
                    if (held > 0) Record(session, candidate, now, null, new { dependency = parent.EpisodeId, undispatchedNotificationsMuted = held, note = "notifications already dispatched are never retracted (spec §16.5)" });
                }
                adopted++;
            }
        }
        return adopted;
    }

    /// <summary>
    /// Release (spec §16.5): the parent closed or the window ended. Dependents become top-level again, their muted rows are coalesced
    /// (never replayed) and every team with a still-firing dependent gets one <c>episode.summary_after_dependency</c>.
    /// </summary>
    public async Task<DependencyReleaseResult> ReleaseDependentsAsync(Episode parent, string trigger, IProcessingSession session, DateTimeOffset now, CancellationToken ct, DateTimeOffset? windowEndedAt = null)
    {
        var dependents = await session.ListUnreleasedDependentsForUpdateAsync(parent.EpisodeId, ct);
        if (windowEndedAt is { } ended) dependents = dependents.Where(d => d.DependencyWindowEndsAt is null || d.DependencyWindowEndsAt <= ended).ToList();
        if (dependents.Count == 0) return new DependencyReleaseResult(0, 0, [], 0);
        var coalesced = 0;
        var byTeam = new Dictionary<Guid, List<Episode>>();
        foreach (var child in dependents)
        {
            child.DependencyReleasedAt = now;
            if (child.SuppressionSource == SuppressionSources.Topology)
            {
                child.SuppressedUntil = null;
                child.SuppressionSource = null;
                coalesced += await session.CoalesceSuppressedOutboxAsync(child.EpisodeId, $"muted as a dependent of {parent.EpisodeId}; not replayed (spec §16.5)", ct);
            }
            child.Touch(now);
            Record(session, child, now, null, new { dependency = parent.EpisodeId, released = true, trigger, detail = "the dependency ended; this is a top-level actionable episode again" });
            if (child.IsOpen && child.IsActionable && child.ConditionState is ConditionState.Firing or ConditionState.Unknown && child.OwningTeamId is { } teamId)
            {
                if (!byTeam.TryGetValue(teamId, out var list)) byTeam[teamId] = list = [];
                list.Add(child);
            }
        }

        var summaryRows = 0;
        var summarised = new List<Guid>();
        var integration = await integrations.GetCurrentAsync(parent.IntegrationId, ct);
        foreach (var (teamId, episodes) in byTeam)
        {
            var team = await teams.GetAsync(teamId, ct);
            var targets = (await destinations.ListForTeamAsync(teamId, ct)).Where(d => d.Active && d.Subscribes(NotificationTypes.EpisodeSummaryAfterDependency)).DistinctBy(d => d.DestinationId).ToList();
            if (team is null || targets.Count == 0) continue;
            var payload = SummaryModel(parent, team, episodes, trigger, now).ToJsonString(JsonDefaults.Stored);
            if (integration is null || !integration.Shadow)
            {
                foreach (var destination in targets)
                {
                    session.AddOutbox(new OutboxMessage { OutboxId = Ids.New(time), EpisodeId = parent.EpisodeId, Type = NotificationTypes.EpisodeSummaryAfterDependency, DestinationId = destination.DestinationId, Payload = payload, NotBefore = now, CreatedAt = now });
                    summaryRows++;
                }
            }
            summarised.Add(teamId);
            foreach (var child in episodes)
            {
                session.AddEpisodeEvent(new EpisodeEvent { Id = Ids.New(time), EpisodeId = child.EpisodeId, At = now, Kind = EpisodeEventKind.SuppressionSummary, Detail = JsonSerializer.Serialize(new { dependency = parent.EpisodeId, teamId, destinations = targets.Count, trigger }, JsonDefaults.Stored) });
            }
        }
        Record(session, parent, now, null, new { dependentsReleased = dependents.Count, trigger, teamsSummarised = summarised.Count });
        return new DependencyReleaseResult(dependents.Count, coalesced, summarised, summaryRows);
    }

    private async Task<Episode?> FindParentAsync(TopologyRule rule, Episode child, IReadOnlyList<string> childKey, IProcessingSession session, DateTimeOffset now, CancellationToken ct)
    {
        var candidates = await session.ListOpenEpisodesOpenedSinceAsync(child.AccessScope, now - rule.Window, CandidateLimit, forUpdate: false, ct);
        var events = await session.LatestNormalisedEventsAsync(candidates.Select(c => c.EpisodeId).ToList(), ct);
        foreach (var candidate in candidates) // newest first: the closest cause wins when several parents qualify
        {
            if (candidate.EpisodeId == child.EpisodeId || !candidate.IsActionable) continue;
            var integration = await integrations.GetCurrentAsync(candidate.IntegrationId, ct);
            var resolve = TopologyMatcher.Resolver(candidate, events.GetValueOrDefault(candidate.EpisodeId), integration);
            if (TopologyMatcher.IsParentOf(rule, candidate, resolve, childKey)) return candidate;
        }
        return null;
    }

    private async Task<TopologyOutcome> AttachAsync(Episode child, Episode parent, TopologyRule rule, IProcessingSession session, DateTimeOffset now, Guid? eventId, bool adopted, CancellationToken ct)
    {
        var windowEnd = parent.FirstSeen + rule.Window;
        if (windowEnd < now + TimeSpan.FromSeconds(1)) windowEnd = now + TimeSpan.FromSeconds(1); // a parent opened with an old timestamp still gets a window from now
        child.DependsOnEpisodeId = parent.EpisodeId;
        child.DependencyRuleId = rule.RuleId;
        child.DependencyAction = rule.Action;
        child.DependencyWindowEndsAt = windowEnd;
        child.DependencyReleasedAt = null;

        var muted = false;
        AlertGroup? group = null;
        var groupNotify = true;
        switch (rule.Action)
        {
            case TopologyActions.MuteChild when rule.Mutes(child.Severity):
                muted = true;
                if (child.SuppressedUntil is null || child.SuppressedUntil < windowEnd)
                {
                    child.SuppressedUntil = windowEnd;
                    child.SuppressionSource = SuppressionSources.Topology;
                }
                break;
            case TopologyActions.GroupUnderParent:
                group = await JoinParentGroupAsync(child, parent, rule, windowEnd, session, now, ct);
                groupNotify = child.Severity == Severity.Critical && !rule.MuteSeverities.Contains(Severity.Critical);
                break;
        }
        if (adopted) child.Touch(now);

        if (!await session.HasPendingJobAsync(parent.EpisodeId, JobKinds.DependencyRelease, windowEnd, ct))
        {
            session.AddJob(new Job
            {
                JobId = Ids.New(time),
                Kind = JobKinds.DependencyRelease,
                NotBefore = windowEnd,
                EpisodeId = parent.EpisodeId,
                IntegrationId = parent.IntegrationId,
                Payload = JsonSerializer.Serialize(new DependencyReleaseJobPayload(parent.EpisodeId, windowEnd), JsonDefaults.Stored),
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        var why = rule.Action switch
        {
            TopologyActions.MuteChild when muted => $"opening notification muted: a consequence of {parent.EpisodeId} under rule '{rule.DisplayName}' (spec §16.5)",
            TopologyActions.MuteChild => $"attributed to {parent.EpisodeId} under rule '{rule.DisplayName}'; severity {child.Severity.ToWire()} is not in mute_severities, so it notifies on its own",
            TopologyActions.GroupUnderParent => $"grouped under {parent.EpisodeId} by rule '{rule.DisplayName}'" + (groupNotify ? "; a critical child still notifies on its own" : " as a quiet member"),
            _ => $"observation mode: recorded as a consequence of {parent.EpisodeId} under rule '{rule.DisplayName}', nothing muted",
        };
        Record(session, child, now, eventId, new { dependency = parent.EpisodeId, rule = rule.DisplayName, ruleId = rule.RuleId, action = rule.Action, muted, adopted, windowEndsAt = windowEnd, groupId = group?.GroupId, why });
        Record(session, parent, now, eventId, new { dependent = child.EpisodeId, rule = rule.DisplayName, action = rule.Action, muted, adopted });
        return new TopologyOutcome(rule, parent, muted, group, groupNotify);
    }

    /// <summary><c>group_under_parent</c>: the parent's group, or a new one keyed on the parent episode with the parent as its first member.</summary>
    private async Task<AlertGroup> JoinParentGroupAsync(Episode child, Episode parent, TopologyRule rule, DateTimeOffset windowEnd, IProcessingSession session, DateTimeOffset now, CancellationToken ct)
    {
        AlertGroup? group = parent.GroupId is { } existing ? await session.FindGroupForUpdateAsync(existing, ct) : null;
        if (group is { IsOpen: false }) group = null;
        if (group is null)
        {
            var key = new JsonObject { ["parent"] = parent.EpisodeId.ToString() }.ToJsonString(JsonDefaults.Stored);
            group = await session.FindOpenGroupForUpdateAsync(rule.RuleId, key, ct);
            if (group is null)
            {
                group = new AlertGroup
                {
                    GroupId = Ids.New(time),
                    AccessScope = parent.AccessScope,
                    RuleId = rule.RuleId,
                    KeyValues = key,
                    OpenedAt = now,
                    WindowEndsAt = windowEnd,
                    Severity = parent.Severity.ToWire(),
                    MemberCount = 1,
                };
                session.AddGroup(group);
                session.AddJob(new Job
                {
                    JobId = Ids.New(time),
                    Kind = JobKinds.GroupWindowClose,
                    NotBefore = windowEnd,
                    IntegrationId = parent.IntegrationId,
                    Payload = JsonSerializer.Serialize(new Grouping.GroupWindowCloseJobPayload(group.GroupId), JsonDefaults.Stored),
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            if (parent.GroupId is null)
            {
                parent.GroupId = group.GroupId;
                parent.Touch(now);
            }
        }
        group.MemberCount++;
        if (child.Severity.Rank() > SeverityExtensions.ParseWire(group.Severity).Rank()) group.Severity = child.Severity.ToWire();
        child.GroupId = group.GroupId;
        session.AddEpisodeEvent(new EpisodeEvent
        {
            Id = Ids.New(time),
            EpisodeId = child.EpisodeId,
            At = now,
            Kind = EpisodeEventKind.Grouped,
            Detail = JsonSerializer.Serialize(new { groupId = group.GroupId, rule = rule.DisplayName, parent = parent.EpisodeId, member = group.MemberCount, first = false, windowEndsAt = group.WindowEndsAt, groupSeverity = group.Severity }, JsonDefaults.Stored),
        });
        return group;
    }

    private JsonObject SummaryModel(Episode parent, Domain.Teams.Team team, IReadOnlyList<Episode> episodes, string trigger, DateTimeOffset now)
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
        var model = NotificationModel.ForHub(NotificationTypes.EpisodeSummaryAfterDependency, new
        {
            parent = new { id = parent.EpisodeId, summary = parent.Summary, severity = parent.Severity.ToWire(), closedAt = parent.ClosedAt, closureReason = parent.ClosureReason, url = $"{options.Value.PublicBaseUrl.TrimEnd('/')}/episodes/{parent.EpisodeId}" },
            trigger,
            team = new { id = team.TeamId, name = team.Name },
            count = episodes.Count,
        });
        model["episodes"] = items;
        model["team"] = new JsonObject { ["id"] = team.TeamId.ToString(), ["name"] = team.Name };
        model["summary"] = $"{episodes.Count} dependent episode(s) still firing after their parent {(trigger.Contains("closed", StringComparison.Ordinal) ? "closed" : "window ended")}";
        model["occurredAt"] = now.ToString("O");
        return model;
    }

    private void Record(IProcessingSession session, Episode episode, DateTimeOffset now, Guid? eventId, object detail)
        => session.AddEpisodeEvent(new EpisodeEvent { Id = Ids.New(time), EpisodeId = episode.EpisodeId, At = now, Kind = EpisodeEventKind.Dependency, EventId = eventId, Detail = JsonSerializer.Serialize(detail, JsonDefaults.Stored) });
}

/// <summary><c>dependency_release</c>: the dependency window of a parent ended; dependents whose window has passed are released with one summary per team.</summary>
public sealed class DependencyReleaseJobHandler(IProcessingUnitOfWork uow, TopologyService topology, TimeProvider time, ILogger<DependencyReleaseJobHandler> logger) : IJobHandler
{
    public string Kind => JobKinds.DependencyRelease;

    public async Task HandleAsync(Job job, JobContext context, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<DependencyReleaseJobPayload>(job.Payload, JsonDefaults.Stored) ?? throw new InvalidOperationException("dependency_release without payload");
        var result = await uow.RunAsync(async session =>
        {
            var parent = await session.FindEpisodeAsync(payload.ParentEpisodeId, ct);
            if (parent is null) return new DependencyReleaseResult(0, 0, [], 0);
            return await topology.ReleaseDependentsAsync(parent, "dependency window ended", session, time.GetUtcNow(), ct, windowEndedAt: payload.WindowEndsAt);
        }, ct);
        if (result.Released > 0) logger.LogInformation("Dependency window of {ParentId} ended: {Released} dependent(s) released, {Teams} team summary(ies)", payload.ParentEpisodeId, result.Released, result.TeamsSummarised.Count);
    }
}
