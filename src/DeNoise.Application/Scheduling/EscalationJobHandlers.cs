using System.Text.Json;
using System.Text.Json.Nodes;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Integrations;
using DeNoise.Application.Notifications;
using DeNoise.Application.Ops;
using DeNoise.Application.Policies;
using DeNoise.Application.Processing;
using DeNoise.Application.Routing;
using DeNoise.Application.Teams;
using DeNoise.Domain.Common;
using DeNoise.Domain.Episodes;
using DeNoise.Domain.Notifications;
using DeNoise.Domain.Ops;
using DeNoise.Domain.Policies;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeNoise.Application.Scheduling;

/// <summary>
/// <c>ack_deadline</c> and <c>escalation_step</c> timers (04 §6 "ack overdue", §7.2). Guards re-read the episode inside the
/// transaction: still open, still <c>new</c>; otherwise nothing is sent. Each step stages outbox rows to its targets and
/// schedules the next step (or a repeat of the last step) in the same commit.
/// </summary>
public sealed class EscalationJobHandler(
    IProcessingUnitOfWork uow, IPolicyRepository policies, ITeamRepository teams, IDestinationRepository destinations,
    IIntegrationRepository integrations, IOptions<NotificationOptions> options, TimeProvider time, ILogger<EscalationJobHandler> logger)
{
    public async Task RunAsync(Job job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<EscalationJobPayload>(job.Payload, JsonDefaults.Stored) ?? throw new InvalidOperationException("escalation job without payload");
        var policyVersion = await policies.GetAsync(PolicyKinds.Escalation, payload.PolicyId, payload.PolicyVersion, ct);
        if (policyVersion is null)
        {
            logger.LogWarning("Escalation policy {PolicyId} v{Version} vanished; step skipped", payload.PolicyId, payload.PolicyVersion);
            return;
        }
        var policy = EscalationPolicyDocument.Parse(JsonNode.Parse(policyVersion.Body)!, policyVersion.Version);

        await uow.RunAsync<object?>(async session =>
        {
            var episode = await session.FindEpisodeAsync(payload.EpisodeId, ct);
            if (episode is null || !episode.IsOpen || episode.HandlingState != HandlingState.New)
            {
                return null; // acknowledged or closed in the meantime: the deadline no longer applies
            }
            var now = time.GetUtcNow();
            var integration = await integrations.GetCurrentAsync(episode.IntegrationId, ct);
            if (integration is null) return null;
            var team = episode.OwningTeamId is { } t ? await teams.GetAsync(t, ct) : null;

            var stepIndex = Math.Min(payload.StepIndex, Math.Max(0, policy.Steps.Count - 1));
            var step = policy.Steps.Count > 0 ? policy.Steps[stepIndex] : new EscalationStep(TimeSpan.Zero, [new EscalationTarget(EscalationTarget.TeamDestinations, null)]);

            var targets = new List<Destination>();
            var external = new List<string>();
            foreach (var target in step.Targets)
            {
                switch (target.Kind)
                {
                    case EscalationTarget.TeamDestinations:
                        if (episode.OwningTeamId is { } teamId) targets.AddRange((await destinations.ListForTeamAsync(teamId, ct)).Where(d => d.Active));
                        break;
                    case EscalationTarget.Destination:
                        if (Guid.TryParse(target.Value, out var did) && await destinations.GetAsync(did, ct) is { Active: true } d) targets.Add(d);
                        break;
                    case EscalationTarget.External:
                        external.Add(target.Value ?? string.Empty); // C7: recorded, resolved by a destination with the same name when one exists
                        var byName = (await destinations.ListAsync(ct)).FirstOrDefault(x => x.Active && string.Equals(x.Name, target.Value, StringComparison.OrdinalIgnoreCase));
                        if (byName is not null) targets.Add(byName);
                        break;
                    case EscalationTarget.User:
                        logger.LogInformation("Escalation to user {UserId} recorded; user notification destinations arrive with milestone 4", target.Value);
                        break;
                }
            }

            var type = job.Kind == JobKinds.AckDeadline ? NotificationTypes.EpisodeAckOverdue : NotificationTypes.EpisodeAckOverdue;
            var model = NotificationModel.ForEpisode(type, episode, null, integration, team, options.Value.PublicBaseUrl,
                escalation: new { step = stepIndex + 1, repeat = payload.Repeat, reason = "ack_overdue", external });
            var body = model.ToJsonString(JsonDefaults.Stored);
            if (!integration.Shadow)
            {
                foreach (var destination in targets.Where(d => d.Subscribes(type)).DistinctBy(d => d.DestinationId))
                {
                    session.AddOutbox(new OutboxMessage
                    {
                        OutboxId = Ids.New(time),
                        EpisodeId = episode.EpisodeId,
                        Type = type,
                        DestinationId = destination.DestinationId,
                        Payload = body,
                        Status = episode.SuppressedUntil is { } u && u > now ? OutboxStatus.Suppressed : OutboxStatus.Pending,
                        NotBefore = now,
                        CreatedAt = now,
                    });
                }
            }
            session.AddEpisodeEvent(new EpisodeEvent
            {
                Id = Ids.New(time),
                EpisodeId = episode.EpisodeId,
                At = now,
                Kind = EpisodeEventKind.Escalate,
                Detail = JsonSerializer.Serialize(new { step = stepIndex + 1, repeat = payload.Repeat, targets = targets.Select(d => d.Name), external }, JsonDefaults.Stored),
            });
            session.AddAudit(new Domain.Audit.AuditEntry
            {
                Id = Ids.New(time),
                At = now,
                ActorType = Domain.Audit.ActorTypes.System,
                ActorId = "scheduler",
                Action = "episode.escalate",
                TargetType = "episode",
                TargetId = episode.EpisodeId.ToString(),
                AccessScope = episode.AccessScope,
                After = JsonSerializer.Serialize(new { step = stepIndex + 1, repeat = payload.Repeat, policyId = payload.PolicyId, policyVersion = payload.PolicyVersion }, JsonDefaults.Stored),
                CorrelationId = job.JobId.ToString("N"),
            });
            episode.Touch(now);

            // Next step, or repeat the last one (04 §7.2).
            var nextIndex = stepIndex + 1;
            if (nextIndex < policy.Steps.Count)
            {
                var delay = policy.Steps[nextIndex].After - step.After;
                ScheduleNext(session, episode, integration.IntegrationId, payload with { StepIndex = nextIndex }, now + (delay > TimeSpan.Zero ? delay : TimeSpan.Zero), now);
            }
            else if (policy.RepeatLastStepEvery is { } every && payload.Repeat < policy.MaxRepeats)
            {
                ScheduleNext(session, episode, integration.IntegrationId, payload with { StepIndex = stepIndex, Repeat = payload.Repeat + 1 }, now + every, now);
            }
            return null;
        }, ct);
    }

    private void ScheduleNext(IProcessingSession session, Episode episode, Guid integrationId, EscalationJobPayload next, DateTimeOffset at, DateTimeOffset now)
        => session.AddJob(new Job
        {
            JobId = Ids.New(time),
            Kind = JobKinds.EscalationStep,
            NotBefore = at,
            EpisodeId = episode.EpisodeId,
            IntegrationId = integrationId,
            ExpectedVersion = episode.Version,
            Payload = JsonSerializer.Serialize(next, JsonDefaults.Stored),
            CreatedAt = now,
            UpdatedAt = now,
        });
}

public sealed class AckDeadlineJobHandler(EscalationJobHandler inner) : IJobHandler
{
    public string Kind => JobKinds.AckDeadline;
    public Task HandleAsync(Job job, JobContext context, CancellationToken ct) => inner.RunAsync(job, ct);
}

public sealed class EscalationStepJobHandler(EscalationJobHandler inner) : IJobHandler
{
    public string Kind => JobKinds.EscalationStep;
    public Task HandleAsync(Job job, JobContext context, CancellationToken ct) => inner.RunAsync(job, ct);
}
