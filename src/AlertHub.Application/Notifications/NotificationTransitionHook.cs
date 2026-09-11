using System.Text.Json;
using System.Text.Json.Nodes;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Integrations;
using AlertHub.Application.Policies;
using AlertHub.Application.Processing;
using AlertHub.Application.Routing;
using AlertHub.Application.Teams;
using AlertHub.Domain.Alerts;
using AlertHub.Domain.Audit;
using AlertHub.Domain.Common;
using AlertHub.Domain.Episodes;
using AlertHub.Domain.Integrations;
using AlertHub.Domain.Notifications;
using AlertHub.Domain.Ops;
using AlertHub.Domain.Policies;
using AlertHub.Domain.Teams;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlertHub.Application.Notifications;

/// <summary>
/// Runs inside the processing transaction (04 §6 triggers): on open → route (04 §7.1), set ownership, stage
/// <c>episode.opened</c> (or <c>episode.routing_failure</c>) outbox rows and the <c>ack_deadline</c> timer; on a
/// material severity increase → <c>episode.escalated_severity</c>; on closure → <c>episode.closed</c> to prior
/// recipients and cancel timers. Shadow integrations get routing but never outbox rows.
/// </summary>
public sealed class NotificationTransitionHook(
    IPolicyRepository policies, ITeamRepository teams, IDestinationRepository destinations, IIntegrationRepository integrations,
    IOptions<NotificationOptions> options, TimeProvider time, ILogger<NotificationTransitionHook> logger) : ITransitionHook
{
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
            case EpisodeTransitionKind.Updated when transition.IsMaterialSeverityIncrease && episode.IsActionable:
                {
                    var team = episode.OwningTeamId is { } t ? await teams.GetAsync(t, ct) : null;
                    var model = NotificationModel.ForEpisode(NotificationTypes.EpisodeEscalatedSeverity, episode, evt, integration, team, options.Value.PublicBaseUrl,
                        previous: new { severity = transition.PreviousSeverity.ToWire() });
                    var targets = await TeamDestinationsAsync(episode.OwningTeamId, ct);
                    Stage(session, episode, NotificationTypes.EpisodeEscalatedSeverity, model, targets, integration, now);
                    break;
                }
            case EpisodeTransitionKind.Resolved:
            case EpisodeTransitionKind.Cancelled:
                {
                    await session.CancelJobsAsync(episode.EpisodeId, [JobKinds.AckDeadline, JobKinds.FollowUp, JobKinds.EscalationStep, JobKinds.AutoResolve, JobKinds.VerifyState, JobKinds.StaleReview, JobKinds.AdminExpiry], ct);
                    var recipients = await session.PriorRecipientsAsync(episode.EpisodeId, ct);
                    if (recipients.Count == 0) break;
                    var team = episode.OwningTeamId is { } t ? await teams.GetAsync(t, ct) : null;
                    var model = NotificationModel.ForEpisode(NotificationTypes.EpisodeClosed, episode, evt, integration, team, options.Value.PublicBaseUrl);
                    var targets = new List<Destination>();
                    foreach (var id in recipients)
                    {
                        var d = await destinations.GetAsync(id, ct);
                        if (d is not null) targets.Add(d);
                    }
                    Stage(session, episode, NotificationTypes.EpisodeClosed, model, targets, integration, now);
                    break;
                }
        }
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

        session.AddEpisodeEvent(new EpisodeEvent
        {
            Id = Ids.New(time),
            EpisodeId = episode.EpisodeId,
            At = now,
            Kind = EpisodeEventKind.Assign,
            EventId = evt.EventId,
            Detail = JsonSerializer.Serialize(new { teamId = decision.TeamId, ruleId = decision.RuleId, ruleName = decision.RuleName, why = decision.Why, correctionRequired = decision.CorrectionRequired, escalationPolicyId = escalationId }, JsonDefaults.Stored),
        });

        if (!episode.IsActionable) return; // informational: queue only (spec §2.1)

        var targets = await TeamDestinationsAsync(decision.TeamId, ct);
        foreach (var extra in decision.ExtraDestinations)
        {
            if (targets.All(d => d.DestinationId != extra) && await destinations.GetAsync(extra, ct) is { } d) targets.Add(d);
        }

        var opened = NotificationModel.ForEpisode(NotificationTypes.EpisodeOpened, episode, evt, integration, team, options.Value.PublicBaseUrl);
        Stage(session, episode, NotificationTypes.EpisodeOpened, opened, targets, integration, now);

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
