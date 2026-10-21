using System.Text.Json;
using System.Text.Json.Nodes;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Integrations;
using DeNoise.Application.Policies;
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
using DeNoise.Domain.Teams;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeNoise.Application.Notifications;

/// <summary>
/// Runs inside the processing transaction (04 §6 triggers): on open → route (04 §7.1), set ownership, stage
/// <c>episode.opened</c> (or <c>episode.routing_failure</c>) outbox rows and the <c>ack_deadline</c> timer; on a
/// material severity increase → <c>episode.escalated_severity</c>; on closure → <c>episode.closed</c> to prior
/// recipients and cancel timers. Shadow integrations get routing but never outbox rows.
/// </summary>
public sealed class NotificationTransitionHook(
    IPolicyRepository policies, ITeamRepository teams, IDestinationRepository destinations, IIntegrationRepository integrations, Lifecycle.LifecycleScheduler lifecycle,
    IOptions<NotificationOptions> options, TimeProvider time, ILogger<NotificationTransitionHook> logger,
    Grouping.GroupingService grouping, Suppressions.SuppressionService suppressions, Topology.TopologyService topology) : ITransitionHook
{
    private static readonly IReadOnlyList<string> AllTimers = [JobKinds.AckDeadline, JobKinds.FollowUp, JobKinds.EscalationStep, JobKinds.AutoResolve, JobKinds.VerifyState, JobKinds.StaleReview, JobKinds.AdminExpiry, JobKinds.InformationalExpiry];

    public async Task OnTransitionAsync(Episode episode, NormalisedEvent evt, EpisodeTransition transition, IProcessingSession session, CancellationToken ct)
    {
        var integration = await integrations.GetCurrentAsync(episode.IntegrationId, ct);
        if (integration is null) return;
        var now = time.GetUtcNow();

        switch (transition.Kind)
        {
            case EpisodeTransitionKind.Opened:
                await OnOpenedAsync(episode, evt, integration, session, now, ct);
                break;
            case EpisodeTransitionKind.Updated:
                {
                    // Every effective signal moves last_seen: reschedule the lifecycle timers from it (04 §2.1 row 2).
                    var effective = await lifecycle.ResolveAsync(episode, integration, null, ct);
                    await lifecycle.ScheduleAsync(episode, integration, effective, session, now, ct);
                    if (transition.IsMaterialSeverityIncrease) await grouping.OnSeverityRaisedAsync(episode, session, ct);
                    if (transition.IsMaterialSeverityIncrease && episode.IsActionable)
                    {
                        var team = episode.OwningTeamId is { } t ? await teams.GetAsync(t, ct) : null;
                        var model = NotificationModel.ForEpisode(NotificationTypes.EpisodeEscalatedSeverity, episode, evt, integration, team, options.Value.PublicBaseUrl,
                            previous: new { severity = transition.PreviousSeverity.ToWire() });
                        var targets = await TeamDestinationsAsync(episode.OwningTeamId, ct);
                        Stage(session, episode, NotificationTypes.EpisodeEscalatedSeverity, model, targets, integration, now);
                    }
                    break;
                }
            case EpisodeTransitionKind.Resolved:
            case EpisodeTransitionKind.Cancelled:
                await CloseAsync(episode, evt, integration, session, now, ct);
                break;
            case EpisodeTransitionKind.Reopened:
                await OnReopenedAsync(episode, evt, integration, session, now, ct);
                break;
        }
    }

    /// <summary>
    /// The held closure notification of a flapping episode is due (spec §12.6): the episode is still closed, so prior recipients get
    /// <c>episode.closed</c> with the evidence recorded at closure. Nothing happens if the episode reopened meanwhile.
    /// </summary>
    public async Task OnHeldClosureDueAsync(Episode episode, IProcessingSession session, CancellationToken ct)
    {
        if (episode.IsOpen || episode.ClosureHoldUntil is null) return;
        var integration = await integrations.GetCurrentAsync(episode.IntegrationId, ct);
        if (integration is null) return;
        var now = time.GetUtcNow();
        var heldSince = episode.ClosureHoldUntil;
        episode.ClosureHoldUntil = null;
        episode.Touch(now);
        session.AddEpisodeEvent(new EpisodeEvent
        {
            Id = Ids.New(time),
            EpisodeId = episode.EpisodeId,
            At = now,
            Kind = EpisodeEventKind.FlapHold,
            Detail = JsonSerializer.Serialize(new { action = "released", dueAt = heldSince, reason = "the hold elapsed with the condition still resolved; the closure notification goes out with its recorded evidence (spec §12.6)" }, JsonDefaults.Stored),
        });
        await StageClosedAsync(episode, null, integration, session, now, ct);
    }

    /// <summary>Closure without a source event (auto-resolve, expiry, coverage): timers cancelled, prior recipients told (04 §6).</summary>
    public async Task OnSystemClosureAsync(Episode episode, IProcessingSession session, CancellationToken ct)
    {
        var integration = await integrations.GetCurrentAsync(episode.IntegrationId, ct);
        if (integration is null) return;
        await CloseAsync(episode, null, integration, session, time.GetUtcNow(), ct);
    }

    private async Task CloseAsync(Episode episode, NormalisedEvent? evt, Integration integration, IProcessingSession session, DateTimeOffset now, CancellationToken ct)
    {
        await session.CancelJobsAsync(episode.EpisodeId, AllTimers, ct);
        // Dependents are released inside the parent's closing transaction (spec §16.5; AGENTS.md §5.4).
        await topology.ReleaseDependentsAsync(episode, "parent closed", session, now, ct);

        // Closure hold (spec §12.6): a flapping episode's closure notification waits for a bounded period; a re-fire inside it cancels the hold.
        if (episode.IsFlapping && episode.ClosureReason is { } reason && Episode.ReopenableClosures.Contains(reason))
        {
            var flap = (await lifecycle.ResolveAsync(episode, integration, null, ct)).Document.Flap;
            if (flap.CloseHold > TimeSpan.Zero && flap.Enabled)
            {
                var holdUntil = now + flap.CloseHold;
                var chainCap = (episode.FlapWindowStartedAt ?? now) + flap.HoldMax; // a chain of holds never spans more than hold_max in total
                if (holdUntil > chainCap) holdUntil = chainCap;
                if (holdUntil > now)
                {
                    episode.ClosureHoldUntil = holdUntil;
                    session.AddJob(new Job
                    {
                        JobId = Ids.New(time),
                        Kind = JobKinds.FlapHold,
                        NotBefore = holdUntil,
                        EpisodeId = episode.EpisodeId,
                        IntegrationId = integration.IntegrationId,
                        ExpectedVersion = episode.Version,
                        Payload = JsonSerializer.Serialize(new FlapHoldJobPayload(episode.EpisodeId), JsonDefaults.Stored),
                        CreatedAt = now,
                        UpdatedAt = now,
                    });
                    session.AddEpisodeEvent(new EpisodeEvent
                    {
                        Id = Ids.New(time),
                        EpisodeId = episode.EpisodeId,
                        At = now,
                        Kind = EpisodeEventKind.FlapHold,
                        EventId = evt?.EventId,
                        Detail = JsonSerializer.Serialize(new { action = "held", until = holdUntil, hold = flap.CloseHold.ToString(), holdMax = flap.HoldMax.ToString(), reopenCount = episode.ReopenCount, reason = "closure recorded now; the notification is held because the condition has been flapping (spec §12.6)" }, JsonDefaults.Stored),
                    });
                    return;
                }
            }
        }
        await StageClosedAsync(episode, evt, integration, session, now, ct);
    }

    private async Task StageClosedAsync(Episode episode, NormalisedEvent? evt, Integration integration, IProcessingSession session, DateTimeOffset now, CancellationToken ct)
    {
        var recipients = await session.PriorRecipientsAsync(episode.EpisodeId, ct);
        if (recipients.Count == 0) return;
        var team = episode.OwningTeamId is { } t ? await teams.GetAsync(t, ct) : null;
        var model = NotificationModel.ForEpisode(NotificationTypes.EpisodeClosed, episode, evt, integration, team, options.Value.PublicBaseUrl);
        var targets = new List<Destination>();
        foreach (var id in recipients)
        {
            var d = await destinations.GetAsync(id, ct);
            if (d is not null) targets.Add(d);
        }
        Stage(session, episode, NotificationTypes.EpisodeClosed, model, targets, integration, now);
    }

    private async Task OnOpenedAsync(Episode episode, NormalisedEvent evt, Integration integration, IProcessingSession session, DateTimeOffset now, CancellationToken ct)
    {
        var routingVersion = await policies.GetActiveAsync(PolicyKinds.Routing, WellKnownPolicies.Routing, ct);
        RoutingPolicyDocument? routing = null;
        if (routingVersion is not null)
        {
            try
            {
                routing = RoutingPolicyDocument.Parse(JsonNode.Parse(routingVersion.Body)!, routingVersion.Version);
            }
            catch (Mapping.MappingValidationException ex)
            {
                logger.LogError(ex, "Active routing policy v{Version} does not parse; routing to triage", routingVersion.Version);
            }
        }
        var allTeams = await teams.ListAsync(ct);
        var triage = allTeams.FirstOrDefault(t => t.IsTriage);
        var decision = RoutingEngine.Route(routing, evt, episode, integration, triage, allTeams.Select(t => t.TeamId).ToHashSet());

        episode.OwningTeamId = decision.TeamId;
        episode.RoutingRuleId = decision.RuleId;
        episode.RoutingCorrectionRequired = decision.CorrectionRequired;
        var team = decision.TeamId is { } teamId ? allTeams.FirstOrDefault(t => t.TeamId == teamId) : null;

        // Escalation policy: rule → team default; ack deadline timer (04 §2.1, §7.2).
        var escalationId = decision.EscalationPolicyId ?? team?.DefaultEscalationPolicyId;
        await ScheduleAckDeadlineAsync(episode, integration, escalationId, session, now, ct);

        session.AddEpisodeEvent(new EpisodeEvent
        {
            Id = Ids.New(time),
            EpisodeId = episode.EpisodeId,
            At = now,
            Kind = EpisodeEventKind.Assign,
            EventId = evt.EventId,
            Detail = JsonSerializer.Serialize(new { teamId = decision.TeamId, ruleId = decision.RuleId, ruleName = decision.RuleName, why = decision.Why, correctionRequired = decision.CorrectionRequired, escalationPolicyId = escalationId }, JsonDefaults.Stored),
        });

        // Lifecycle timers (04 §2.1 row 1): auto_resolve per policy, expiry per spec §12.5; informational gets its retention timer.
        var effectiveLifecycle = await lifecycle.ResolveAsync(episode, integration, decision.LifecyclePolicyId, ct);
        await lifecycle.ScheduleAsync(episode, integration, effectiveLifecycle, session, now, ct);

        if (!episode.IsActionable) return; // informational: queue only (spec §2.1)

        // Suppression (04 §2.3), dependency (spec §16.5) and grouping (04 §7.4) decide how the opened notification is staged: muted, skipped as a quiet group member, or sent.
        await suppressions.ApplyToNewEpisodeAsync(episode, evt, integration, session, now, ct);
        var dependency = await topology.ApplyAsync(episode, evt, integration, session, now, ct);
        var grouped = dependency.Group is not null
            ? new Grouping.GroupingOutcome(dependency.Group, false, dependency.GroupNotify, dependency.Rule?.DisplayName)
            : await grouping.ApplyAsync(episode, evt, integration, session, now, ct);

        var targets = await TeamDestinationsAsync(decision.TeamId, ct);
        foreach (var extra in decision.ExtraDestinations)
        {
            if (targets.All(d => d.DestinationId != extra) && await destinations.GetAsync(extra, ct) is { } d) targets.Add(d);
        }

        if (grouped.Notify)
        {
            var opened = NotificationModel.ForEpisode(NotificationTypes.EpisodeOpened, episode, evt, integration, team, options.Value.PublicBaseUrl,
                previous: grouped.Group is null ? null : new { groupId = grouped.Group.GroupId, groupSeverity = grouped.Group.Severity, groupMembers = grouped.Group.MemberCount, groupRule = grouped.RuleName });
            Stage(session, episode, NotificationTypes.EpisodeOpened, opened, targets, integration, now);
        }

        // Parent side (spec §16.5): children that fired first inside the look-back are adopted now.
        await topology.AdoptAsync(episode, evt, integration, session, now, ct);

        if (decision.IsRoutingFailure)
        {
            var failure = NotificationModel.ForEpisode(NotificationTypes.EpisodeRoutingFailure, episode, evt, integration, team, options.Value.PublicBaseUrl,
                escalation: new { reason = "routing_failure", why = decision.Why });
            Stage(session, episode, NotificationTypes.EpisodeRoutingFailure, failure, targets, integration, now);
            session.AddAudit(new AuditEntry
            {
                Id = Ids.New(time),
                At = now,
                ActorType = ActorTypes.System,
                ActorId = "routing",
                Action = "episode.routing_failure",
                TargetType = "episode",
                TargetId = episode.EpisodeId.ToString(),
                AccessScope = episode.AccessScope,
                After = JsonSerializer.Serialize(new { decision.Why, teamId = decision.TeamId }, JsonDefaults.Stored),
                CorrelationId = evt.EventId.ToString("N"),
            });
        }
    }

    /// <summary>
    /// Reopen (spec §11.4): ownership, handling and the acknowledgement deadline are kept; lifecycle timers restart from the new signal;
    /// suppression and grouping apply as to a new episode; <c>episode.opened</c> is staged only as <c>notify_on_reopen</c> allows.
    /// </summary>
    private async Task OnReopenedAsync(Episode episode, NormalisedEvent evt, Integration integration, IProcessingSession session, DateTimeOffset now, CancellationToken ct)
    {
        var effective = await lifecycle.ResolveAsync(episode, integration, null, ct);
        await lifecycle.ScheduleAsync(episode, integration, effective, session, now, ct);
        var team = episode.OwningTeamId is { } t ? await teams.GetAsync(t, ct) : null;

        // The deadline is not restarted (spec §11.4): an unacknowledged episode keeps its original one; if it has passed, the overdue escalation runs now.
        if (episode.HandlingState == HandlingState.New && episode.AckDeadlineAt is { } deadline && episode.IsActionable)
        {
            var escalationId = team?.DefaultEscalationPolicyId;
            if (escalationId is { } eid && await policies.GetActiveAsync(PolicyKinds.Escalation, eid, ct) is { } ev)
            {
                session.AddJob(new Job
                {
                    JobId = Ids.New(time),
                    Kind = JobKinds.AckDeadline,
                    NotBefore = deadline > now ? deadline : now,
                    EpisodeId = episode.EpisodeId,
                    IntegrationId = integration.IntegrationId,
                    ExpectedVersion = episode.Version,
                    Payload = JsonSerializer.Serialize(new EscalationJobPayload(episode.EpisodeId, eid, ev.Version, 0, 0), JsonDefaults.Stored),
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
        }

        if (!episode.IsActionable) return;
        await suppressions.ApplyToNewEpisodeAsync(episode, evt, integration, session, now, ct);
        var dependency = await topology.ApplyAsync(episode, evt, integration, session, now, ct);
        var grouped = dependency.Group is not null
            ? new Grouping.GroupingOutcome(dependency.Group, false, dependency.GroupNotify, dependency.Rule?.DisplayName)
            : await grouping.ApplyAsync(episode, evt, integration, session, now, ct);
        await topology.AdoptAsync(episode, evt, integration, session, now, ct);
        var flap = effective.Document.Flap;
        var notify = flap.NotifyOnReopen switch
        {
            Lifecycle.NotifyOnReopen.Every => true,
            Lifecycle.NotifyOnReopen.Never => false,
            _ => episode.ReopenCount == 1,
        };
        if (notify && grouped.Notify)
        {
            var model = NotificationModel.ForEpisode(NotificationTypes.EpisodeOpened, episode, evt, integration, team, options.Value.PublicBaseUrl,
                previous: new { reopened = true, reopenCount = episode.ReopenCount, flapping = episode.IsFlapping, groupId = grouped.Group?.GroupId, groupRule = grouped.RuleName });
            Stage(session, episode, NotificationTypes.EpisodeOpened, model, await TeamDestinationsAsync(episode.OwningTeamId, ct), integration, now);
        }
        else
        {
            session.AddEpisodeEvent(new EpisodeEvent
            {
                Id = Ids.New(time),
                EpisodeId = episode.EpisodeId,
                At = now,
                Kind = EpisodeEventKind.Notify,
                EventId = evt.EventId,
                Detail = JsonSerializer.Serialize(new { type = NotificationTypes.EpisodeOpened, skipped = true, reason = notify ? "quiet group member" : $"notify_on_reopen is '{flap.NotifyOnReopen}' and this is reopen #{episode.ReopenCount} (spec §11.4)" }, JsonDefaults.Stored),
            });
        }
    }

    /// <summary>Heartbeat miss episodes: ownership comes from the definition, so routing rules are skipped (spec §13.3.3 "owned and routed like any other" — by its declared team).</summary>
    public async Task OnPreassignedOpenAsync(Episode episode, NormalisedEvent evt, Integration integration, Team team, string notificationType, object? detail, IProcessingSession session, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        episode.OwningTeamId = team.TeamId;
        episode.RoutingRuleId = null;
        episode.RoutingCorrectionRequired = false;
        await ScheduleAckDeadlineAsync(episode, integration, team.DefaultEscalationPolicyId, session, now, ct);
        session.AddEpisodeEvent(new EpisodeEvent
        {
            Id = Ids.New(time),
            EpisodeId = episode.EpisodeId,
            At = now,
            Kind = EpisodeEventKind.Assign,
            EventId = evt.EventId,
            Detail = JsonSerializer.Serialize(new { teamId = team.TeamId, ruleId = (Guid?)null, ruleName = (string?)null, why = $"ownership set by the heartbeat definition → team '{team.Name}'", correctionRequired = false, escalationPolicyId = team.DefaultEscalationPolicyId }, JsonDefaults.Stored),
        });
        var effectiveLifecycle = await lifecycle.ResolveAsync(episode, integration, null, ct);
        await lifecycle.ScheduleAsync(episode, integration, effectiveLifecycle, session, now, ct);

        var model = NotificationModel.ForEpisode(notificationType, episode, evt, integration, team, options.Value.PublicBaseUrl, escalation: detail);
        if (detail is not null) model["heartbeat"] = JsonSerializer.SerializeToNode(detail, JsonDefaults.Stored);
        Stage(session, episode, notificationType, model, await TeamDestinationsAsync(team.TeamId, ct), integration, now);
    }

    private async Task ScheduleAckDeadlineAsync(Episode episode, Integration integration, Guid? escalationId, IProcessingSession session, DateTimeOffset now, CancellationToken ct)
    {
        EscalationPolicyDocument? escalation = null;
        int? escalationVersion = null;
        if (escalationId is { } eid && await policies.GetActiveAsync(PolicyKinds.Escalation, eid, ct) is { } ev)
        {
            try
            {
                escalation = EscalationPolicyDocument.Parse(JsonNode.Parse(ev.Body)!, ev.Version);
                escalationVersion = ev.Version;
            }
            catch (Mapping.MappingValidationException ex)
            {
                logger.LogError(ex, "Escalation policy {PolicyId} v{Version} does not parse; no deadline scheduled", eid, ev.Version);
            }
        }
        if (episode.IsActionable && escalation?.AckDeadline is { } deadline)
        {
            episode.AckDeadlineAt = now + deadline;
            session.AddJob(new Job
            {
                JobId = Ids.New(time),
                Kind = JobKinds.AckDeadline,
                NotBefore = episode.AckDeadlineAt.Value,
                EpisodeId = episode.EpisodeId,
                IntegrationId = integration.IntegrationId,
                ExpectedVersion = episode.Version,
                Payload = JsonSerializer.Serialize(new EscalationJobPayload(episode.EpisodeId, escalationId!.Value, escalationVersion!.Value, 0, 0), JsonDefaults.Stored),
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
    }

    private async Task<List<Destination>> TeamDestinationsAsync(Guid? teamId, CancellationToken ct)
        => teamId is { } t ? (await destinations.ListForTeamAsync(t, ct)).Where(d => d.Active).ToList() : [];

    /// <summary>One outbox row per subscribed destination; suppressed episodes get rows in status <c>suppressed</c> (04 §2.3); shadow integrations get none.</summary>
    private void Stage(IProcessingSession session, Episode episode, string type, JsonObject model, IEnumerable<Destination> targets, Integration integration, DateTimeOffset now)
    {
        if (integration.Shadow) return;
        var suppressed = episode.SuppressedUntil is { } until && until > now;
        var payload = model.ToJsonString(JsonDefaults.Stored);
        foreach (var destination in targets.Where(d => d.Subscribes(type)).DistinctBy(d => d.DestinationId))
        {
            session.AddOutbox(new OutboxMessage
            {
                OutboxId = Ids.New(time),
                EpisodeId = episode.EpisodeId,
                Type = type,
                DestinationId = destination.DestinationId,
                Payload = payload,
                Status = suppressed ? OutboxStatus.Suppressed : OutboxStatus.Pending,
                NotBefore = now,
                CreatedAt = now,
            });
        }
    }
}

/// <summary>Payload of <c>ack_deadline</c> / <c>escalation_step</c> jobs.</summary>
public sealed record EscalationJobPayload(Guid EpisodeId, Guid PolicyId, int PolicyVersion, int StepIndex, int Repeat);

/// <summary>Payload of the <c>flap_hold</c> timer (spec §12.6).</summary>
public sealed record FlapHoldJobPayload(Guid EpisodeId);

/// <summary><c>flap_hold</c>: the held closure notification is due. Reopened episodes cancelled the job; a stale one finds the episode open and does nothing.</summary>
public sealed class FlapHoldJobHandler(IProcessingUnitOfWork uow, ITransitionHook hook, IEpisodeChangePublisher publisher, ILogger<FlapHoldJobHandler> logger) : Ops.IJobHandler
{
    public string Kind => JobKinds.FlapHold;

    public async Task HandleAsync(Job job, Ops.JobContext context, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<FlapHoldJobPayload>(job.Payload, JsonDefaults.Stored) ?? throw new InvalidOperationException("flap_hold without payload");
        var changed = await uow.RunAsync(async session =>
        {
            var episode = await session.FindEpisodeAsync(payload.EpisodeId, ct);
            if (episode is null || episode.IsOpen || episode.ClosureHoldUntil is null) return null;
            await hook.OnHeldClosureDueAsync(episode, session, ct);
            return episode;
        }, ct);
        if (changed is not null)
        {
            try
            {
                await publisher.PublishAsync(changed, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "change publish after flap_hold failed; clients resync");
            }
        }
    }
}
