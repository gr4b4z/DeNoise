using System.Text.Json;
using System.Text.Json.Nodes;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Mapping;
using AlertHub.Application.Ops;
using AlertHub.Application.Policies;
using AlertHub.Application.Processing;
using AlertHub.Application.Routing;
using AlertHub.Domain.Alerts;
using AlertHub.Domain.Common;
using AlertHub.Domain.Episodes;
using AlertHub.Domain.Integrations;
using AlertHub.Domain.Ops;
using AlertHub.Domain.Policies;
using Microsoft.Extensions.Logging;

namespace AlertHub.Application.Grouping;

/// <summary>What grouping decided for a newly opened episode.</summary>
public sealed record GroupingOutcome(AlertGroup? Group, bool IsFirstMember, bool Notify, string? RuleName)
{
    public static readonly GroupingOutcome None = new(null, true, true, null);
}

public sealed record GroupWindowCloseJobPayload(Guid GroupId);

/// <summary>
/// Grouping at open time (04 §7.4, spec §16.3): the first matching rule of the active grouping policy computes the key from the
/// event; an open group for (rule, key) within its window adopts the episode, otherwise a new group starts a fixed window. The
/// group's severity is the max active member; a new critical child stays visible and may notify on its own.
/// </summary>
public sealed class GroupingService(IPolicyRepository policies, TimeProvider time, ILogger<GroupingService> logger)
{
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

    public async Task<GroupingOutcome> ApplyAsync(Episode episode, NormalisedEvent evt, Integration integration, IProcessingSession session, DateTimeOffset now, CancellationToken ct)
    {
        var policy = await ActiveAsync(ct);
        if (policy is null) return GroupingOutcome.None;
        JsonNode? Resolve(string reference) => RoutingEngine.ResolveRef(reference, evt, episode, integration);
        var rule = policy.Rules.FirstOrDefault(r => PredicateEvaluator.Evaluate(r.Match, Resolve));
        if (rule is null) return GroupingOutcome.None;

        var key = new JsonObject();
        foreach (var field in rule.Key.OrderBy(k => k, StringComparer.Ordinal))
        {
            var value = Resolve(field);
            if (value is null) return GroupingOutcome.None; // a key field without a value cannot group (the key would be ambiguous)
            key[field] = value.DeepClone();
        }
        var keyJson = key.ToJsonString(JsonDefaults.Stored);

        var group = await session.FindOpenGroupForUpdateAsync(rule.RuleId, keyJson, ct);
        if (group is not null && (group.WindowEndsAt <= now || group.AccessScope != episode.AccessScope))
        {
            group = null; // the window has passed (the close job may not have run yet) or another scope: never join across scopes
        }
        var first = group is null;
        if (group is null)
        {
            group = new AlertGroup
            {
                GroupId = Ids.New(time),
                AccessScope = episode.AccessScope,
                RuleId = rule.RuleId,
                KeyValues = keyJson,
                OpenedAt = now,
                WindowEndsAt = now + rule.Window,
                Severity = episode.Severity.ToWire(),
                MemberCount = 1,
            };
            session.AddGroup(group);
            session.AddJob(new Job
            {
                JobId = Ids.New(time),
                Kind = JobKinds.GroupWindowClose,
                NotBefore = group.WindowEndsAt,
                IntegrationId = episode.IntegrationId,
                Payload = JsonSerializer.Serialize(new GroupWindowCloseJobPayload(group.GroupId), JsonDefaults.Stored),
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            group.MemberCount++;
            if (episode.Severity.Rank() > SeverityExtensions.ParseWire(group.Severity).Rank()) group.Severity = episode.Severity.ToWire();
        }
        episode.GroupId = group.GroupId;
        var notify = first || (rule.Notify == GroupNotify.FirstAndNewCritical && episode.Severity == Severity.Critical);
        session.AddEpisodeEvent(new EpisodeEvent
        {
            Id = Ids.New(time),
            EpisodeId = episode.EpisodeId,
            At = now,
            Kind = EpisodeEventKind.Grouped,
            EventId = evt.EventId,
            Detail = JsonSerializer.Serialize(new { groupId = group.GroupId, rule = rule.Name ?? rule.RuleId.ToString(), key, member = group.MemberCount, first, notify, windowEndsAt = group.WindowEndsAt, groupSeverity = group.Severity }, JsonDefaults.Stored),
        });
        return new GroupingOutcome(group, first, notify, rule.Name);
    }

    /// <summary>A member's severity rose: the group carries the max active member (04 §7.4).</summary>
    public async Task OnSeverityRaisedAsync(Episode episode, IProcessingSession session, CancellationToken ct)
    {
        if (episode.GroupId is not { } groupId) return;
        var group = await session.FindGroupForUpdateAsync(groupId, ct);
        if (group is null || !group.IsOpen) return;
        if (episode.Severity.Rank() > SeverityExtensions.ParseWire(group.Severity).Rank()) group.Severity = episode.Severity.ToWire();
    }
}

/// <summary><c>group_window_close</c>: the fixed window ended; the group stops adopting members (04 §7.4). Members are untouched.</summary>
public sealed class GroupWindowCloseJobHandler(IProcessingUnitOfWork uow, TimeProvider time) : IJobHandler
{
    public string Kind => JobKinds.GroupWindowClose;

    public async Task HandleAsync(Job job, JobContext context, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<GroupWindowCloseJobPayload>(job.Payload, JsonDefaults.Stored) ?? throw new InvalidOperationException("group_window_close without payload");
        await uow.RunAsync<object?>(async session =>
        {
            var group = await session.FindGroupForUpdateAsync(payload.GroupId, ct);
            if (group is { IsOpen: true }) group.ClosedAt = time.GetUtcNow();
            return null;
        }, ct);
    }
}
