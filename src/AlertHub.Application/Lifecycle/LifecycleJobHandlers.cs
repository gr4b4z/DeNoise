using System.Text.Json;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Coverage;
using AlertHub.Application.Integrations;
using AlertHub.Application.Notifications;
using AlertHub.Application.Ops;
using AlertHub.Application.Processing;
using AlertHub.Application.Realtime;
using AlertHub.Application.Teams;
using AlertHub.Domain.Audit;
using AlertHub.Domain.Common;
using AlertHub.Domain.Episodes;
using AlertHub.Domain.Integrations;
using AlertHub.Domain.Notifications;
using AlertHub.Domain.Ops;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlertHub.Application.Lifecycle;

public sealed class LifecycleOptions
{
    public const string Section = "Lifecycle";
    /// <summary>Guard 4 (04 §5.3): a <c>normalise</c> backlog older than this postpones inference.</summary>
    public TimeSpan ProcessingDelayThreshold { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>Guard 5: this many mapping failures within <see cref="MappingFailureWindow"/> postpones inference.</summary>
    public int MappingFailureThreshold { get; set; } = 5;
    public TimeSpan MappingFailureWindow { get; set; } = TimeSpan.FromMinutes(15);
    /// <summary>How long a postponed closure waits before the guards are re-evaluated.</summary>
    public TimeSpan PostponeBy { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// The lifecycle timers of 04 §11: <c>auto_resolve</c> with the eight guards of 04 §5.3, <c>verify_state</c>, <c>stale_review</c>,
/// <c>admin_expiry</c> and <c>informational_expiry</c>. Every decision is recorded on the timeline so the UI can explain it.
/// </summary>
public sealed class LifecycleJobHandler(
    IProcessingUnitOfWork uow, LifecycleScheduler scheduler, IIntegrationRepository integrations, ITeamRepository teams, IDestinationRepository destinations,
    IStateQueryAdapter stateQuery, ITransitionHook hook, IEpisodeChangePublisher publisher, IOptions<LifecycleOptions> options, IOptions<NotificationOptions> notifications,
    TimeProvider time, ILogger<LifecycleJobHandler> logger)
{
    public async Task RunAsync(Job job, JobContext context, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<LifecycleJobPayload>(job.Payload, JsonDefaults.Stored) ?? throw new InvalidOperationException("lifecycle job without payload");
        var changed = await uow.RunAsync(async session =>
        {
            var episode = await session.FindEpisodeAsync(payload.EpisodeId, ct);
            if (episode is null || !episode.IsOpen) return null;
            var integration = await integrations.GetCurrentAsync(episode.IntegrationId, ct);
            if (integration is null) return null;
            var now = time.GetUtcNow();
            return job.Kind switch
            {
                JobKinds.AutoResolve => await AutoResolveAsync(job, context, payload, episode, integration, session, now, ct),
                JobKinds.VerifyState => await VerifyStateAsync(job, episode, integration, session, now, ct),
                JobKinds.StaleReview => await StaleReviewAsync(job, episode, session, now, ct),
                JobKinds.AdminExpiry => await AdminExpiryAsync(job, context, payload, episode, integration, session, now, ct),
                JobKinds.InformationalExpiry => await InformationalExpiryAsync(job, episode, session, now, ct),
                _ => null,
            };
        }, ct);
        if (changed is not null)
        {
            try
            {
                await publisher.PublishAsync(changed, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "change publish after lifecycle job failed; clients resync");
            }
        }
    }

    /// <summary>Guards 1–8 of 04 §5.3, evaluated inside the transaction that closes.</summary>
    private async Task<Episode?> AutoResolveAsync(Job job, JobContext context, LifecycleJobPayload payload, Episode episode, Integration integration, IProcessingSession session, DateTimeOffset now, CancellationToken ct)
    {
        // Guard 1: a stale timer cannot close an updated episode. A newer signal re-staged its own timer; this one is obsolete.
        if (job.ExpectedVersion is { } ev && ev != episode.Version && job.ExpectedLastSeen is { } els && els != episode.LastSeen)
        {
            Record(session, episode, EpisodeEventKind.Postpone, now, new { reason = "stale_timer", detail = "a newer signal rescheduled this deadline; nothing to do" });
            return null;
        }
        if (episode.ConditionState is not (ConditionState.Firing or ConditionState.Unknown)) return null;

        // Guard 6: policy still active and still resolving automatically (re-resolved, so a deactivated policy stops the timer).
        var lifecycle = await scheduler.ResolveAsync(episode, integration, null, ct);
        var doc = lifecycle.Document;
        if (!doc.AutoResolve.Enabled || doc.Profile == LifecycleProfiles.Coverage)
        {
            episode.AutoResolveAt = null;
            Record(session, episode, EpisodeEventKind.Postpone, now, new { reason = "auto_resolve_disabled", policy = lifecycle.Source });
            return episode;
        }

        // Guard 2: due, recomputed from the current last_seen.
        var due = episode.LastSeen + doc.InactivityTimeout;
        if (now < due)
        {
            await RescheduleAsync(session, job, context, episode, payload, due, ct);
            return episode;
        }

        // Guard 3: coverage lost since the timer was set → suspend, condition unknown (spec §12.4: silence has a competing explanation).
        var coverage = await session.GetCoverageForUpdateAsync(integration.IntegrationId, ct);
        var coverageState = coverage?.State ?? CoverageStates.Unknown;
        if (CoverageStates.IsLost(coverageState))
        {
            if (doc.AutoResolve.OnCoverageDegraded == CoverageUnknownBehaviour.Suspend)
            {
                await SuspendAsync(session, job, context, episode, now, $"coverage {coverageState}", ct);
                episode.MarkConditionUnknown(now);
                return episode;
            }
        }
        else if (coverageState is CoverageStates.Unknown or CoverageStates.NotConfigured && doc.AutoResolve.OnCoverageUnknown == CoverageUnknownBehaviour.Suspend)
        {
            await SuspendAsync(session, job, context, episode, now, "coverage never established and the policy says suspend", ct);
            return episode;
        }

        // Guard 4: accepted events still unprocessed for this integration.
        var oldest = await session.OldestPendingNormaliseAsync(integration.IntegrationId, ct);
        if (oldest is { } o && now - o > options.Value.ProcessingDelayThreshold)
        {
            await PostponeAsync(session, job, context, episode, payload, now, $"processing backlog: oldest unprocessed event is {(now - o).TotalMinutes:F0} min old", ct);
            return episode;
        }

        // Guard 5: mapping health (auth failure arrives with the source adapters in milestone 8).
        var failures = await session.CountMappingFailuresAsync(integration.IntegrationId, now - options.Value.MappingFailureWindow, ct);
        if (failures >= options.Value.MappingFailureThreshold)
        {
            await PostponeAsync(session, job, context, episode, payload, now, $"{failures} mapping failures in the last {options.Value.MappingFailureWindow.TotalMinutes:F0} min", ct);
            return episode;
        }

        // Guard 7: verification with the source where configured and possible.
        var evidence = coverageState == CoverageStates.Healthy && coverage is not null && coverage.Since <= episode.LastSeen && doc.Profile != LifecycleProfiles.Unknown
            ? Evidence.HeartbeatAndInactivity
            : Evidence.InactivityUnverified;
        var reason = ClosureReason.InactivityTimeout;
        if (doc.AutoResolve.VerifyBeforeClose != VerifyBeforeClose.None && HasStateQuery(integration))
        {
            var result = await stateQuery.QueryAsync(integration, episode, ct);
            switch (result.Outcome)
            {
                case StateQueryOutcome.Active:
                    episode.LastVerifiedAt = now;
                    Record(session, episode, EpisodeEventKind.Postpone, now, new { reason = "source_still_active", detail = result.Detail });
                    await RescheduleAsync(session, job, context, episode, payload, now + doc.InactivityTimeout, ct);
                    return episode;
                case StateQueryOutcome.NotActive:
                    episode.LastVerifiedAt = now;
                    reason = ClosureReason.VerifiedResolved;
                    evidence = Evidence.ApiVerification;
                    break;
                case StateQueryOutcome.Error when doc.AutoResolve.VerifyBeforeClose == VerifyBeforeClose.ApiRequired:
                    await SuspendAsync(session, job, context, episode, now, $"state query failed and verification is required: {result.Detail}", ct);
                    return episode;
                default:
                    break; // api_if_available: continue with inference
            }
        }

        // Guard 8: critical/high get a stale-state escalation to the owning team before inference closes them.
        if (doc.AutoResolve.EscalateBeforeCloseIfSeverity.Contains(episode.Severity) && !payload.StaleEscalationSent && reason == ClosureReason.InactivityTimeout)
        {
            await StageStaleEscalationAsync(episode, integration, session, now, doc, ct);
            await RescheduleAsync(session, job, context, episode, payload with { StaleEscalationSent = true }, now + doc.AutoResolve.StaleEscalationLead, ct);
            return episode;
        }

        episode.CloseBySystem(reason, evidence, now);
        Record(session, episode, EpisodeEventKind.AutoResolve, now, new
        {
            reason,
            evidence,
            silence = (now - episode.LastSeen).ToString(),
            timeout = doc.InactivityTimeout.ToString(),
            coverage = coverageState,
            policy = lifecycle.Source,
        });
        Audit(session, episode, "episode.auto_resolve", now, new { reason, evidence, coverage = coverageState, policyId = lifecycle.PolicyId, policyVersion = lifecycle.PolicyVersion }, job.JobId);
        await hook.OnSystemClosureAsync(episode, session, ct);
        return episode;
    }

    private async Task<Episode?> VerifyStateAsync(Job job, Episode episode, Integration integration, IProcessingSession session, DateTimeOffset now, CancellationToken ct)
    {
        var result = HasStateQuery(integration) ? await stateQuery.QueryAsync(integration, episode, ct) : new StateQueryResult(StateQueryOutcome.Unsupported, "integration has no state_query capability");
        Record(session, episode, EpisodeEventKind.SourceEvent, now, new { verification = result.Outcome.ToString().ToLowerInvariant(), detail = result.Detail });
        if (result.Outcome == StateQueryOutcome.NotActive)
        {
            episode.LastVerifiedAt = now;
            episode.CloseBySystem(ClosureReason.VerifiedResolved, Evidence.ApiVerification, now);
            Audit(session, episode, "episode.verified_resolved", now, new { detail = result.Detail }, job.JobId);
            await hook.OnSystemClosureAsync(episode, session, ct);
        }
        else if (result.Outcome == StateQueryOutcome.Active)
        {
            episode.LastVerifiedAt = now;
            episode.Touch(now);
        }
        return episode;
    }

    private Task<Episode?> StaleReviewAsync(Job job, Episode episode, IProcessingSession session, DateTimeOffset now, CancellationToken ct)
    {
        _ = ct;
        if (job.ExpectedLastSeen is { } els && els != episode.LastSeen) return Task.FromResult<Episode?>(null); // a newer signal re-staged the review
        if (episode.StaleSince is null)
        {
            episode.StaleSince = now;
            episode.Touch(now);
            Record(session, episode, EpisodeEventKind.StaleReview, now, new { detail = "no verification for the review window; listed under Stale / unverified (spec §12.5)" });
            Audit(session, episode, "episode.stale_review", now, new { since = now }, job.JobId);
        }
        return Task.FromResult<Episode?>(episode);
    }

    private async Task<Episode?> AdminExpiryAsync(Job job, JobContext context, LifecycleJobPayload payload, Episode episode, Integration integration, IProcessingSession session, DateTimeOffset now, CancellationToken ct)
    {
        // Guards 1/2 of 04 §5.3 apply to expiry too: a newer signal moved last_seen, so this timer is stale.
        if (job.ExpectedLastSeen is { } els && els != episode.LastSeen)
        {
            Record(session, episode, EpisodeEventKind.Postpone, now, new { reason = "stale_timer", detail = "a newer signal rescheduled the expiry; nothing to do" });
            return null;
        }
        var lifecycle = await scheduler.ResolveAsync(episode, integration, null, ct);
        var doc = lifecycle.Document;
        if (doc.Expiry.UnverifiedExpireAfter is not { } expireAfter || doc.Expiry.NeverExpireSeverities.Contains(episode.Severity))
        {
            Record(session, episode, EpisodeEventKind.Postpone, now, new { reason = "expiry_not_permitted", policy = lifecycle.Source });
            return episode;
        }
        var due = episode.LastSeen + expireAfter;
        if (now < due)
        {
            await session.RescheduleJobAsync(job.JobId, due, episode.Version, episode.LastSeen, JsonSerializer.Serialize(payload, JsonDefaults.Stored), ct);
            context.Retained = true;
            return episode;
        }
        // The last known condition is preserved: expiry is not recovery (spec §11.2, §12.5).
        episode.CloseBySystem(ClosureReason.ExpiredUnverified, Evidence.None, now, "administrative expiry after " + expireAfter.ToString());
        Record(session, episode, EpisodeEventKind.Expire, now, new { reason = ClosureReason.ExpiredUnverified, evidence = Evidence.None, lastKnownCondition = episode.ConditionState, after = expireAfter.ToString(), policy = lifecycle.Source });
        Audit(session, episode, "episode.expire", now, new { lastKnownCondition = episode.ConditionState, policyId = lifecycle.PolicyId, policyVersion = lifecycle.PolicyVersion }, job.JobId);
        await hook.OnSystemClosureAsync(episode, session, ct);
        return episode;
    }

    private async Task<Episode?> InformationalExpiryAsync(Job job, Episode episode, IProcessingSession session, DateTimeOffset now, CancellationToken ct)
    {
        if (job.ExpectedLastSeen is { } els && els != episode.LastSeen) return null;
        episode.CloseBySystem(ClosureReason.InformationalCompleted, Evidence.None, now);
        Record(session, episode, EpisodeEventKind.Expire, now, new { reason = ClosureReason.InformationalCompleted });
        Audit(session, episode, "episode.informational_completed", now, null, job.JobId);
        await hook.OnSystemClosureAsync(episode, session, ct);
        return episode;
    }

    private static bool HasStateQuery(Integration integration)
        => integration.Capabilities.Contains("\"state_query\"", StringComparison.Ordinal) && !integration.Capabilities.Contains("\"state_query\":false", StringComparison.Ordinal);

    /// <summary>Moves the claimed timer to a new time (same row: one live timer per episode and kind) and keeps it out of the runner's completion.</summary>
    private async Task RescheduleAsync(IProcessingSession session, Job job, JobContext context, Episode episode, LifecycleJobPayload payload, DateTimeOffset at, CancellationToken ct)
    {
        episode.AutoResolveAt = at;
        episode.Touch(time.GetUtcNow());
        await session.RescheduleJobAsync(job.JobId, at, episode.Version, episode.LastSeen, JsonSerializer.Serialize(payload, JsonDefaults.Stored), ct);
        context.Retained = true;
    }

    private async Task PostponeAsync(IProcessingSession session, Job job, JobContext context, Episode episode, LifecycleJobPayload payload, DateTimeOffset now, string why, CancellationToken ct)
    {
        Record(session, episode, EpisodeEventKind.Postpone, now, new { reason = "integration_unhealthy", detail = why, retryAt = now + options.Value.PostponeBy });
        await RescheduleAsync(session, job, context, episode, payload, now + options.Value.PostponeBy, ct);
    }

    /// <summary>The claimed row becomes <c>suspended</c> so the UI shows it and coverage recovery can resume it (04 §2.1).</summary>
    private async Task SuspendAsync(IProcessingSession session, Job job, JobContext context, Episode episode, DateTimeOffset now, string why, CancellationToken ct)
    {
        await session.SuspendJobAsync(job.JobId, ct);
        context.Retained = true;
        Record(session, episode, EpisodeEventKind.Suspend, now, new { reason = why });
        episode.Touch(now);
    }

    private async Task StageStaleEscalationAsync(Episode episode, Integration integration, IProcessingSession session, DateTimeOffset now, LifecyclePolicyDocument doc, CancellationToken ct)
    {
        var team = episode.OwningTeamId is { } t ? await teams.GetAsync(t, ct) : null;
        var model = NotificationModel.ForEpisode(NotificationTypes.EpisodeStaleCritical, episode, null, integration, team, notifications.Value.PublicBaseUrl,
            escalation: new { reason = "stale_state", closesAt = (now + doc.AutoResolve.StaleEscalationLead).ToString("O"), silence = (now - episode.LastSeen).ToString() });
        var payload = model.ToJsonString(JsonDefaults.Stored);
        if (!integration.Shadow && episode.OwningTeamId is { } teamId)
        {
            foreach (var destination in (await destinations.ListForTeamAsync(teamId, ct)).Where(d => d.Active && d.Subscribes(NotificationTypes.EpisodeStaleCritical)).DistinctBy(d => d.DestinationId))
            {
                session.AddOutbox(new OutboxMessage
                {
                    OutboxId = Ids.New(time),
                    EpisodeId = episode.EpisodeId,
                    Type = NotificationTypes.EpisodeStaleCritical,
                    DestinationId = destination.DestinationId,
                    Payload = payload,
                    Status = episode.SuppressedUntil is { } u && u > now ? OutboxStatus.Suppressed : OutboxStatus.Pending,
                    NotBefore = now,
                    CreatedAt = now,
                });
            }
        }
        Record(session, episode, EpisodeEventKind.Escalate, now, new { reason = "stale_state", lead = doc.AutoResolve.StaleEscalationLead.ToString(), detail = "no signal for the inactivity window; the owning team is warned before inference closes it" });
        episode.Touch(now);
    }

    private void Record(IProcessingSession session, Episode episode, string kind, DateTimeOffset now, object detail)
        => session.AddEpisodeEvent(new EpisodeEvent { Id = Ids.New(time), EpisodeId = episode.EpisodeId, At = now, Kind = kind, Detail = JsonSerializer.Serialize(detail, JsonDefaults.Stored) });

    private void Audit(IProcessingSession session, Episode episode, string action, DateTimeOffset now, object? after, Guid jobId)
        => session.AddAudit(new AuditEntry
        {
            Id = Ids.New(time),
            At = now,
            ActorType = ActorTypes.System,
            ActorId = "scheduler",
            Action = action,
            TargetType = "episode",
            TargetId = episode.EpisodeId.ToString(),
            AccessScope = episode.AccessScope,
            After = after is null ? null : JsonSerializer.Serialize(after, JsonDefaults.Stored),
            CorrelationId = jobId.ToString("N"),
        });
}

public sealed class AutoResolveJobHandler(LifecycleJobHandler inner) : IJobHandler
{
    public string Kind => JobKinds.AutoResolve;
    public Task HandleAsync(Job job, JobContext context, CancellationToken ct) => inner.RunAsync(job, context, ct);
}

public sealed class VerifyStateJobHandler(LifecycleJobHandler inner) : IJobHandler
{
    public string Kind => JobKinds.VerifyState;
    public Task HandleAsync(Job job, JobContext context, CancellationToken ct) => inner.RunAsync(job, context, ct);
}

public sealed class StaleReviewJobHandler(LifecycleJobHandler inner) : IJobHandler
{
    public string Kind => JobKinds.StaleReview;
    public Task HandleAsync(Job job, JobContext context, CancellationToken ct) => inner.RunAsync(job, context, ct);
}

public sealed class AdminExpiryJobHandler(LifecycleJobHandler inner) : IJobHandler
{
    public string Kind => JobKinds.AdminExpiry;
    public Task HandleAsync(Job job, JobContext context, CancellationToken ct) => inner.RunAsync(job, context, ct);
}

public sealed class InformationalExpiryJobHandler(LifecycleJobHandler inner) : IJobHandler
{
    public string Kind => JobKinds.InformationalExpiry;
    public Task HandleAsync(Job job, JobContext context, CancellationToken ct) => inner.RunAsync(job, context, ct);
}
