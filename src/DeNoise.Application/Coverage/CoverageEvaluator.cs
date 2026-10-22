using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Integrations;
using DeNoise.Application.Lifecycle;
using DeNoise.Application.Notifications;
using DeNoise.Application.Processing;
using DeNoise.Application.Realtime;
using DeNoise.Application.Teams;
using DeNoise.Domain.Alerts;
using DeNoise.Domain.Audit;
using DeNoise.Domain.Common;
using DeNoise.Domain.Episodes;
using DeNoise.Domain.Integrations;
using DeNoise.Domain.Notifications;
using DeNoise.Domain.Ops;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeNoise.Application.Coverage;

/// <summary>Receives canary/heartbeat signals from the processing transaction (04 §2.1: <c>heartbeat</c> never touches episodes, only coverage).</summary>
public interface ICoverageSignalSink
{
    Task OnSignalAsync(Integration integration, NormalisedEvent evt, IProcessingSession session, DateTimeOffset now, CancellationToken ct);
}

/// <summary>Outcome of a coverage evaluation for one integration, for callers that want to report it.</summary>
public sealed record CoverageEvaluation(Guid IntegrationId, CoverageStep Step, int SuspendedJobs, Guid? CoverageEpisodeId);

/// <summary>
/// Drives the coverage state machine (04 §9) and its consequences inside one transaction per integration: the coverage
/// episode (one per outage, spec §13.6), suspension/resumption of inactivity timers and the <c>unknown</c>/<c>firing</c>
/// flip of every open episode (04 §2.1). Signals arrive from processing; the scheduler ticks the time-driven transitions.
/// </summary>
public sealed class CoverageEvaluator(
    IProcessingUnitOfWork uow, IIntegrationRepository integrations, ITeamRepository teams, IDestinationRepository destinations,
    IChangeBroadcaster broadcaster, IEpisodeChangePublisher publisher, IOptions<NotificationOptions> options, TimeProvider time, ILogger<CoverageEvaluator> logger) : ICoverageSignalSink
{
    public static readonly IReadOnlyList<string> SuspendableKinds = [JobKinds.AutoResolve, JobKinds.VerifyState];

    public async Task OnSignalAsync(Integration integration, NormalisedEvent evt, IProcessingSession session, DateTimeOffset now, CancellationToken ct)
    {
        var config = CoverageConfig.Parse(integration.Coverage);
        var state = await session.GetCoverageForUpdateAsync(integration.IntegrationId, ct);
        if (state is null)
        {
            state = new CoverageState { IntegrationId = integration.IntegrationId, State = CoverageStates.Unknown, Since = now };
            session.AddCoverage(state);
        }
        state.LastSignalAt = now;
        if (config.Canary is null)
        {
            // No method configured: the signal is remembered but coverage is never "established" (spec §12.4, never established vs failing).
            state.ConsecutiveSuccesses++;
            return;
        }
        var failure = evt.Labels is { } labels && labels.TryGetValue("canary", out var flag) && string.Equals(flag, "fail", StringComparison.OrdinalIgnoreCase);
        var step = CoverageMachine.OnSignal(state, config.Canary, now, success: !failure);
        await ApplyStepAsync(integration, state, step, session, now, ct);
    }

    /// <summary>
    /// A heartbeat bound to the integration drives its coverage (spec §13.3.3): a miss is an explicit coverage failure, a recovery a success.
    /// Without a canary method the heartbeat's own <c>recovery_successes_required</c> is the threshold. Returns the episodes touched.
    /// </summary>
    public async Task<IReadOnlyList<Episode>> OnBoundHeartbeatAsync(Guid integrationId, bool success, int recoveryRequired, IProcessingSession session, DateTimeOffset now, CancellationToken ct)
    {
        var integration = await integrations.GetCurrentAsync(integrationId, ct);
        if (integration is null) return [];
        var config = CoverageConfig.Parse(integration.Coverage);
        var method = config.Canary ?? new CanaryMethod(TimeSpan.MaxValue, TimeSpan.MaxValue, TimeSpan.MaxValue, TimeSpan.MaxValue, Math.Max(1, recoveryRequired));
        var state = await session.GetCoverageForUpdateAsync(integrationId, ct);
        if (state is null)
        {
            state = new CoverageState { IntegrationId = integrationId, State = CoverageStates.Unknown, Since = now };
            session.AddCoverage(state);
        }
        if (success) state.LastSignalAt = now;
        var step = CoverageMachine.OnSignal(state, method, now, success);
        var (_, episodes) = await ApplyStepAsync(integration, state, step, session, now, ct);
        if (step.Changed) _pendingAnnouncements.Add((integration, step.To));
        return episodes;
    }

    private readonly List<(Integration Integration, string State)> _pendingAnnouncements = [];

    /// <summary>Announces coverage changes recorded by <see cref="OnBoundHeartbeatAsync"/> once the caller has committed.</summary>
    public async Task FlushAnnouncementsAsync(CancellationToken ct)
    {
        foreach (var (integration, state) in _pendingAnnouncements) await AnnounceAsync(integration, state, [], ct);
        _pendingAnnouncements.Clear();
    }

    /// <summary>Scheduler tick: evaluates every current integration with a canary method. Returns the integrations whose state changed.</summary>
    public async Task<IReadOnlyList<CoverageEvaluation>> TickAllAsync(CancellationToken ct)
    {
        var changed = new List<CoverageEvaluation>();
        foreach (var integration in await integrations.ListCurrentAsync(ct))
        {
            if (!integration.Active) continue;
            var evaluation = await TickAsync(integration, ct);
            if (evaluation is { Step.Changed: true }) changed.Add(evaluation);
        }
        return changed;
    }

    /// <summary>
    /// Records an <c>api_probe</c> outcome (spec §13.5). A failed probe is an explicit coverage failure like a missed bound
    /// heartbeat; a successful one proves reachability only and never raises coverage (the canary or heartbeat does that).
    /// </summary>
    public async Task<IReadOnlyList<Episode>> OnApiProbeAsync(Integration integration, ProbeResult probe, CancellationToken ct)
    {
        if (!probe.Supported || probe.Ok) return [];
        try
        {
            var episodes = await uow.RunAsync<IReadOnlyList<Episode>>(session => OnBoundHeartbeatAsync(integration.IntegrationId, false, 1, session, time.GetUtcNow(), ct), ct);
            foreach (var episode in episodes) await publisher.PublishAsync(episode, ct);
            await FlushAnnouncementsAsync(ct);
            return episodes;
        }
        catch (ProcessingConflictException ex)
        {
            logger.LogDebug(ex, "api probe result for {IntegrationId} lost a race; next probe retries", integration.IntegrationId);
            return [];
        }
    }

    public async Task<CoverageEvaluation?> TickAsync(Integration integration, CancellationToken ct)
    {
        var config = CoverageConfig.Parse(integration.Coverage);
        if (config.Canary is null) return null;
        try
        {
            var (evaluation, episodes) = await uow.RunAsync<(CoverageEvaluation? Evaluation, IReadOnlyList<Episode> Episodes)>(async session =>
            {
                var now = time.GetUtcNow();
                var state = await session.GetCoverageForUpdateAsync(integration.IntegrationId, ct);
                if (state is null) return (null, []);
                var step = CoverageMachine.OnTick(state, config.Canary, now);
                var touched = await ApplyStepAsync(integration, state, step, session, now, ct);
                return (new CoverageEvaluation(integration.IntegrationId, step, touched.Suspended, state.CoverageEpisodeId), touched.Episodes);
            }, ct);
            if (evaluation is { Step.Changed: true }) await AnnounceAsync(integration, evaluation.Step.To, episodes, ct);
            return evaluation;
        }
        catch (ProcessingConflictException ex)
        {
            logger.LogDebug(ex, "coverage tick for {IntegrationId} lost a race; next tick retries", integration.IntegrationId);
            return null;
        }
    }

    /// <summary>Applies the consequences of a transition. Public so the processing path can announce after its own commit.</summary>
    private async Task<(int Suspended, IReadOnlyList<Episode> Episodes)> ApplyStepAsync(Integration integration, CoverageState state, CoverageStep step, IProcessingSession session, DateTimeOffset now, CancellationToken ct)
    {
        if (!step.Changed) return (0, []);
        var touched = new List<Episode>();
        var suspended = 0;
        if (step.Lost)
        {
            suspended = await session.SuspendJobsAsync(integration.IntegrationId, SuspendableKinds, ct);
            foreach (var episode in await session.ListOpenEpisodesForIntegrationForUpdateAsync(integration.IntegrationId, ct))
            {
                if (episode.LifecycleProfile == LifecycleProfiles.Coverage) continue;
                if (episode.MarkConditionUnknown(now))
                {
                    Record(session, episode, EpisodeEventKind.Coverage, now, new { coverage = step.To, detail = "coverage of the source is lost; the condition is unknown and inactivity closure is suspended (spec §12.4)" });
                    touched.Add(episode);
                }
            }
            var coverageEpisode = await EnsureCoverageEpisodeAsync(integration, state, session, now, suspended, ct);
            state.CoverageEpisodeId = coverageEpisode.EpisodeId;
            state.Detail = JsonSerializer.Serialize(new { suspendedEpisodes = suspended, lastProcessedAlertAt = state.LastProcessedAlertAt }, JsonDefaults.Stored);
            touched.Add(coverageEpisode);
        }
        else if (step.Worsened && state.CoverageEpisodeId is { } worseId && await session.FindEpisodeAsync(worseId, ct) is { IsOpen: true } worse)
        {
            if (worse.RaiseSeverity(Severity.Critical, now))
            {
                Record(session, worse, EpisodeEventKind.Coverage, now, new { coverage = step.To, detail = "coverage considered lost; severity raised" });
                touched.Add(worse);
            }
        }
        else if (step.Restored)
        {
            var resumed = await session.ResumeJobsAsync(integration.IntegrationId, SuspendableKinds, now, ct);
            foreach (var episode in await session.ListOpenEpisodesForIntegrationForUpdateAsync(integration.IntegrationId, ct))
            {
                if (episode.LifecycleProfile == LifecycleProfiles.Coverage) continue;
                if (episode.MarkConditionFiring(now))
                {
                    Record(session, episode, EpisodeEventKind.Coverage, now, new { coverage = step.To, detail = "coverage restored; the last known condition applies again and inactivity closure resumes" });
                    touched.Add(episode);
                }
            }
            if (state.CoverageEpisodeId is { } id && await session.FindEpisodeAsync(id, ct) is { IsOpen: true } coverageEpisode)
            {
                coverageEpisode.CloseBySystem(ClosureReason.SourceResolved, Evidence.Source, now, $"{Math.Max(1, resumed)} suspended closure(s) resumed");
                Record(session, coverageEpisode, EpisodeEventKind.Coverage, now, new { coverage = step.To, resumedJobs = resumed, detail = "consecutive successful signals reached the configured threshold" });
                await StageAsync(session, coverageEpisode, integration, NotificationTypes.CoverageRestored, new { state = step.To, resumedJobs = resumed }, now, ct);
                touched.Add(coverageEpisode);
            }
            state.CoverageEpisodeId = null;
            state.Detail = null;
        }
        session.AddAudit(new AuditEntry
        {
            Id = Ids.New(time),
            At = now,
            ActorType = ActorTypes.System,
            ActorId = "coverage",
            Action = "integration.coverage_changed",
            TargetType = "integration",
            TargetId = integration.IntegrationId.ToString(),
            AccessScope = integration.AccessScope,
            Before = JsonSerializer.Serialize(new { state = step.From }, JsonDefaults.Stored),
            After = JsonSerializer.Serialize(new { state = step.To, suspended }, JsonDefaults.Stored),
            CorrelationId = Ids.New(time).ToString("N"),
        });
        return (suspended, touched);
    }

    /// <summary>One deduplicated coverage episode per outage (spec §13.6): identity <c>coverage:{integration_id}</c>, owner = integration owner or triage.</summary>
    private async Task<Episode> EnsureCoverageEpisodeAsync(Integration integration, CoverageState state, IProcessingSession session, DateTimeOffset now, int suspended, CancellationToken ct)
    {
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"coverage:{integration.IntegrationId}")));
        var existing = await session.FindOpenEpisodeForUpdateAsync(fingerprint, ct);
        if (existing is not null) return existing;

        var allTeams = await teams.ListAsync(ct);
        var owner = integration.OwnerTeamId is { } o && allTeams.Any(t => t.TeamId == o) ? integration.OwnerTeamId : allTeams.FirstOrDefault(t => t.IsTriage)?.TeamId;
        var lastSignal = state.LastSignalAt?.ToString("u") ?? "never";
        var evt = new NormalisedEvent
        {
            EventId = Ids.New(time),
            IntegrationId = integration.IntegrationId,
            RawReceivedAt = now,
            ReceivedAt = now,
            OccurredAt = now,
            EventType = EventTypes.Firing,
            Severity = Severity.High,
            SourceAlertId = $"coverage:{integration.IntegrationId}",
            ResourceId = integration.IntegrationId.ToString(),
            ResourceName = integration.Name,
            RuleId = "coverage",
            RuleName = "Monitoring coverage",
            Service = "denoise",
            Environment = integration.AccessScope,
            Summary = $"Monitoring coverage lost for {integration.Name}. Last verified signal: {lastSignal}. Current alerts may be stale. Silence-based resolution is suspended for {suspended} episode(s).",
            DeliveryKey = $"coverage:{integration.IntegrationId}:{now:O}",
            IdentityConfidence = "exact",
            Fingerprint = fingerprint,
            IdentityComponents = [new IdentityComponent("kind", "coverage"), new IdentityComponent("integration", integration.IntegrationId.ToString())],
            LifecycleProfileHint = LifecycleProfiles.Coverage,
        };
        await session.EnsureIdentityAsync(new AlertIdentity
        {
            Fingerprint = fingerprint,
            IntegrationId = integration.IntegrationId,
            AccessScope = integration.AccessScope,
            IdentityVersion = 0,
            Components = JsonSerializer.Serialize(evt.IdentityComponents, JsonDefaults.Stored),
            FirstSeen = now,
        }, ct);
        var latestClosed = await session.FindLatestClosedEpisodeAsync(fingerprint, ct);
        var episode = Episode.Open(evt, integration.AccessScope, now, time, latestClosed?.EpisodeId);
        episode.OwningTeamId = owner;
        episode.LifecycleProfile = LifecycleProfiles.Coverage;
        session.AddEpisode(episode);
        await session.IncrementIdentityEpisodeCountAsync(fingerprint, ct);
        Record(session, episode, EpisodeEventKind.SourceEvent, now, new { eventType = "coverage.lost", transition = "opened", suspendedEpisodes = suspended, lastSignalAt = state.LastSignalAt });
        session.AddAudit(new AuditEntry
        {
            Id = Ids.New(time),
            At = now,
            ActorType = ActorTypes.System,
            ActorId = "coverage",
            Action = "episode.open",
            TargetType = "episode",
            TargetId = episode.EpisodeId.ToString(),
            AccessScope = episode.AccessScope,
            After = JsonSerializer.Serialize(new { kind = "coverage", integrationId = integration.IntegrationId, suspended }, JsonDefaults.Stored),
            CorrelationId = evt.EventId.ToString("N"),
        });
        await StageAsync(session, episode, integration, NotificationTypes.CoverageLost, new { state = CoverageStates.Degraded, suspendedEpisodes = suspended, lastSignalAt = state.LastSignalAt }, now, ct);
        return episode;
    }

    /// <summary>Coverage notifications go to the integration owner team's destinations, or to triage when there is no owner (spec §13.6).</summary>
    private async Task StageAsync(IProcessingSession session, Episode episode, Integration integration, string type, object detail, DateTimeOffset now, CancellationToken ct)
    {
        if (integration.Shadow) return;
        var team = episode.OwningTeamId is { } t ? await teams.GetAsync(t, ct) : null;
        var targets = team is null ? [] : (await destinations.ListForTeamAsync(team.TeamId, ct)).Where(d => d.Active && d.Subscribes(type)).DistinctBy(d => d.DestinationId).ToList();
        var model = NotificationModel.ForEpisode(type, episode, null, integration, team, options.Value.PublicBaseUrl, escalation: detail);
        var payload = model.ToJsonString(JsonDefaults.Stored);
        foreach (var destination in targets)
        {
            session.AddOutbox(new OutboxMessage
            {
                OutboxId = Ids.New(time),
                EpisodeId = episode.EpisodeId,
                Type = type,
                DestinationId = destination.DestinationId,
                Payload = payload,
                Status = OutboxStatus.Pending,
                NotBefore = now,
                CreatedAt = now,
            });
        }
    }

    private async Task AnnounceAsync(Integration integration, string state, IReadOnlyList<Episode> episodes, CancellationToken ct)
    {
        try
        {
            await broadcaster.PublishAsync(new ChangeEvent(ChangeEvent.CoverageChanged, integration.AccessScope, JsonSerializer.Serialize(new { integrationId = integration.IntegrationId, state }, JsonDefaults.Stored)), ct);
            foreach (var episode in episodes) await publisher.PublishAsync(episode, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "coverage change broadcast failed; clients resync");
        }
    }

    private void Record(IProcessingSession session, Episode episode, string kind, DateTimeOffset now, object detail)
        => session.AddEpisodeEvent(new EpisodeEvent { Id = Ids.New(time), EpisodeId = episode.EpisodeId, At = now, Kind = kind, Detail = JsonSerializer.Serialize(detail, JsonDefaults.Stored) });
}
