using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Coverage;
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
using DeNoise.Domain.Heartbeats;
using DeNoise.Domain.Integrations;
using DeNoise.Domain.Notifications;
using DeNoise.Domain.Ops;
using DeNoise.Domain.Teams;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeNoise.Application.Heartbeats;

/// <summary>What one heartbeat evaluation touched, for post-commit announcements.</summary>
public sealed record HeartbeatOutcome(Heartbeat Heartbeat, HeartbeatTransition Transition, IReadOnlyList<Episode> Episodes);

/// <summary>
/// The consequences of a heartbeat transition inside the processing transaction (04 §10 "Effect" column): the miss episode —
/// one ordinary alert episode per outage with the same ownership and closure semantics as any other — its resolution with
/// evidence <c>source</c>, pause closing an open miss, and the coverage of a bound integration (spec §13.3.3).
/// </summary>
public sealed class HeartbeatEffects(
    IIntegrationRepository integrations, ITeamRepository teams, IDestinationRepository destinations, ITransitionHook hook, CoverageEvaluator coverage,
    IChangeBroadcaster broadcaster, IEpisodeChangePublisher publisher, IOptions<NotificationOptions> notifications, TimeProvider time, ILogger<HeartbeatEffects> logger)
{
    public async Task<IReadOnlyList<Episode>> ApplyAsync(Heartbeat hb, HeartbeatTransition transition, IProcessingSession session, DateTimeOffset now, string? detail, CancellationToken ct, bool? pingSuccess = null)
    {
        var touched = new List<Episode>();
        // A bound heartbeat is the integration's coverage signal (spec §13.3.3): every successful ping counts towards recovery, a miss is a failure.
        if (pingSuccess == true && hb.BindsToIntegrationId is { } boundOk) touched.AddRange(await coverage.OnBoundHeartbeatAsync(boundOk, true, hb.RecoverySuccessesRequired, session, now, ct));
        if (!transition.Changed) return touched;
        if (transition.Missed)
        {
            var episode = await EnsureMissEpisodeAsync(hb, session, now, detail, ct);
            if (episode is not null) touched.Add(episode);
            if (hb.BindsToIntegrationId is { } bound) touched.AddRange(await coverage.OnBoundHeartbeatAsync(bound, false, hb.RecoverySuccessesRequired, session, now, ct));
        }
        else if (transition.Recovered)
        {
            if (hb.MissEpisodeId is { } missId && await session.FindEpisodeAsync(missId, ct) is { IsOpen: true } miss)
            {
                // Genuine confirmed recovery: the job pinged (spec §13.3.3), so evidence is `source`, not inference.
                var evt = MissEvent(hb, now, "recovered");
                miss.ApplySourceResolved(evt, now);
                Record(session, miss, EpisodeEventKind.SourceEvent, now, new { eventType = "heartbeat.recovered", detail = detail ?? "ping received" });
                await StageAsync(session, miss, hb, NotificationTypes.HeartbeatRecovered, new { state = hb.State, lastPingAt = hb.LastPingAt }, now, ct);
                await hook.OnSystemClosureAsync(miss, session, ct);
                touched.Add(miss);
            }
            hb.MissEpisodeId = null;
        }
        else if (transition.Paused)
        {
            // 04 §10: pause closes an open miss episode as manual_close and is reported as a coverage gap (paused ≠ healthy).
            if (hb.MissEpisodeId is { } missId && await session.FindEpisodeAsync(missId, ct) is { IsOpen: true } miss)
            {
                miss.CloseManually($"heartbeat paused: {hb.PauseReason}", now);
                Record(session, miss, EpisodeEventKind.Close, now, new { reason = ClosureReason.ManualClose, detail = $"heartbeat paused ({hb.PauseReason})", pausedBy = hb.PausedBy });
                await hook.OnSystemClosureAsync(miss, session, ct);
                touched.Add(miss);
            }
            hb.MissEpisodeId = null;
        }
        session.AddAudit(new AuditEntry
        {
            Id = Ids.New(time),
            At = now,
            ActorType = ActorTypes.System,
            ActorId = "heartbeat",
            Action = "heartbeat.state_changed",
            TargetType = "heartbeat",
            TargetId = hb.HeartbeatId.ToString(),
            AccessScope = hb.AccessScope,
            Before = JsonSerializer.Serialize(new { state = transition.From }, JsonDefaults.Stored),
            After = JsonSerializer.Serialize(new { state = transition.To, detail }, JsonDefaults.Stored),
            CorrelationId = Ids.New(time).ToString("N"),
        });
        return touched;
    }

    /// <summary>Best-effort after commit: <c>heartbeat.changed</c> for the UI plus the touched episodes.</summary>
    public async Task AnnounceAsync(Heartbeat hb, IReadOnlyList<Episode> episodes, CancellationToken ct)
    {
        try
        {
            await broadcaster.PublishAsync(new ChangeEvent(ChangeEvent.HeartbeatChanged, hb.AccessScope, JsonSerializer.Serialize(new { id = hb.HeartbeatId, state = hb.State, version = hb.Version, expectedNext = hb.ExpectedNext, lastPingAt = hb.LastPingAt }, JsonDefaults.Stored)), ct);
            foreach (var episode in episodes) await publisher.PublishAsync(episode, ct);
            await coverage.FlushAnnouncementsAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "heartbeat change broadcast failed; clients resync");
        }
    }

    public static string Fingerprint(Guid heartbeatId) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"heartbeat:{heartbeatId}")));

    private async Task<Episode?> EnsureMissEpisodeAsync(Heartbeat hb, IProcessingSession session, DateTimeOffset now, string? detail, CancellationToken ct)
    {
        var fingerprint = Fingerprint(hb.HeartbeatId);
        var open = await session.FindOpenEpisodeForUpdateAsync(fingerprint, ct);
        if (open is not null)
        {
            // Still one episode per outage, however many intervals are skipped (spec §22).
            hb.MissEpisodeId = open.EpisodeId;
            Record(session, open, EpisodeEventKind.SourceEvent, now, new { eventType = "heartbeat.missed", detail = detail ?? "still missing", note = "recorded on the open episode; no new episode per missed interval" });
            return null;
        }
        var integrationId = hb.BindsToIntegrationId ?? WellKnownIntegrations.Heartbeats;
        var integration = await integrations.GetCurrentAsync(integrationId, ct) ?? await integrations.GetCurrentAsync(WellKnownIntegrations.Heartbeats, ct);
        var team = await teams.GetAsync(hb.OwningTeamId, ct);
        if (integration is null || team is null)
        {
            logger.LogError("Heartbeat {HeartbeatId} missed but its integration or team is gone; no episode opened", hb.HeartbeatId);
            return null;
        }
        var evt = MissEvent(hb, now, detail ?? "no ping within the grace period");
        await session.EnsureIdentityAsync(new AlertIdentity
        {
            Fingerprint = fingerprint,
            IntegrationId = integration.IntegrationId,
            AccessScope = hb.AccessScope,
            IdentityVersion = 0,
            Components = JsonSerializer.Serialize(evt.IdentityComponents, JsonDefaults.Stored),
            FirstSeen = now,
        }, ct);
        var previous = await session.FindLatestClosedEpisodeAsync(fingerprint, ct);
        var episode = Episode.Open(evt, hb.AccessScope, now, time, previous?.EpisodeId);
        episode.OwningTeamId = hb.OwningTeamId;
        episode.AssigneeId = hb.AssigneeId;
        episode.LifecycleProfile = LifecycleProfiles.Heartbeat;
        session.AddEpisode(episode);
        await session.IncrementIdentityEpisodeCountAsync(fingerprint, ct);
        hb.MissEpisodeId = episode.EpisodeId;
        Record(session, episode, EpisodeEventKind.SourceEvent, now, new { eventType = "heartbeat.missed", transition = "opened", detail = evt.Summary, expectedNext = hb.ExpectedNext, grace = hb.Grace.ToString(), lastPingAt = hb.LastPingAt });
        session.AddAudit(new AuditEntry
        {
            Id = Ids.New(time),
            At = now,
            ActorType = ActorTypes.System,
            ActorId = "heartbeat",
            Action = "episode.open",
            TargetType = "episode",
            TargetId = episode.EpisodeId.ToString(),
            AccessScope = episode.AccessScope,
            After = JsonSerializer.Serialize(new { kind = "heartbeat", heartbeatId = hb.HeartbeatId, severity = hb.SeverityOnMiss }, JsonDefaults.Stored),
            CorrelationId = evt.EventId.ToString("N"),
        });
        // Ownership comes from the heartbeat definition; the hook stages ack deadline + `heartbeat.missed` to the team's destinations.
        await hook.OnPreassignedOpenAsync(episode, evt, integration, team, NotificationTypes.HeartbeatMissed, HeartbeatDetail(hb), session, ct);
        return episode;
    }

    private async Task StageAsync(IProcessingSession session, Episode episode, Heartbeat hb, string type, object detail, DateTimeOffset now, CancellationToken ct)
    {
        var integration = await integrations.GetCurrentAsync(episode.IntegrationId, ct);
        var team = await teams.GetAsync(hb.OwningTeamId, ct);
        if (integration is null || team is null) return;
        var model = NotificationModel.ForEpisode(type, episode, null, integration, team, notifications.Value.PublicBaseUrl, escalation: detail);
        model["heartbeat"] = JsonSerializer.SerializeToNode(HeartbeatDetail(hb), JsonDefaults.Stored);
        var payload = model.ToJsonString(JsonDefaults.Stored);
        foreach (var destination in (await destinations.ListForTeamAsync(team.TeamId, ct)).Where(d => d.Active && d.Subscribes(type)).DistinctBy(d => d.DestinationId))
        {
            session.AddOutbox(new OutboxMessage { OutboxId = Ids.New(time), EpisodeId = episode.EpisodeId, Type = type, DestinationId = destination.DestinationId, Payload = payload, Status = OutboxStatus.Pending, NotBefore = now, CreatedAt = now });
        }
    }

    private static object HeartbeatDetail(Heartbeat hb) => new
    {
        id = hb.HeartbeatId,
        name = hb.Name,
        state = hb.State,
        schedule = HeartbeatSchedule.Describe(hb),
        grace = hb.Grace.ToString(),
        expectedNext = hb.ExpectedNext,
        lastPingAt = hb.LastPingAt,
        boundIntegrationId = hb.BindsToIntegrationId,
    };

    private NormalisedEvent MissEvent(Heartbeat hb, DateTimeOffset now, string detail)
    {
        var last = hb.LastPingAt?.ToString("u") ?? "never";
        return new NormalisedEvent
        {
            EventId = Ids.New(time),
            IntegrationId = hb.BindsToIntegrationId ?? WellKnownIntegrations.Heartbeats,
            RawReceivedAt = now,
            ReceivedAt = now,
            OccurredAt = now,
            EventType = EventTypes.Firing,
            Severity = SeverityExtensions.ParseWire(hb.SeverityOnMiss),
            SourceAlertId = $"heartbeat:{hb.HeartbeatId}",
            ResourceId = hb.HeartbeatId.ToString(),
            ResourceName = hb.Name,
            RuleId = "heartbeat",
            RuleName = "Heartbeat missed",
            Service = "heartbeat",
            Environment = hb.AccessScope,
            Summary = $"Heartbeat '{hb.Name}' missed: last ping {last}, expected by {hb.ExpectedNext?.ToString("u") ?? "n/a"} (grace {hb.Grace}). {detail}",
            DeliveryKey = $"heartbeat:{hb.HeartbeatId}:{now:O}",
            IdentityConfidence = "exact",
            Fingerprint = Fingerprint(hb.HeartbeatId),
            IdentityComponents = [new IdentityComponent("kind", "heartbeat"), new IdentityComponent("heartbeat", hb.HeartbeatId.ToString())],
            LifecycleProfileHint = LifecycleProfiles.Heartbeat,
        };
    }

    private void Record(IProcessingSession session, Episode episode, string kind, DateTimeOffset now, object detail)
        => session.AddEpisodeEvent(new EpisodeEvent { Id = Ids.New(time), EpisodeId = episode.EpisodeId, At = now, Kind = kind, Detail = JsonSerializer.Serialize(detail, JsonDefaults.Stored) });
}

/// <summary>The ping path (06 §3): verify, apply the state machine, store the run — one short transaction, no raw event, no job.</summary>
public sealed class HeartbeatPingService(IHeartbeatPingAuthenticator authenticator, IProcessingUnitOfWork uow, HeartbeatEffects effects, IOptions<HeartbeatOptions> options, TimeProvider time)
{
    /// <summary>Returns false for an unknown key or wrong token (the endpoint answers 404 either way).</summary>
    public async Task<bool> PingAsync(string credential, string runKind, int? exitCode, string? body, System.Net.IPAddress? sourceIp, CancellationToken ct = default)
    {
        var dot = credential.IndexOf('.');
        if (dot <= 0 || dot == credential.Length - 1) return false;
        var hb = await authenticator.AuthenticateAsync(credential[..dot], credential[(dot + 1)..], ct);
        if (hb is null) return false;

        var stored = Tail(body, options.Value.BodyBytes);
        var outcome = await uow.RunAsync(async session =>
        {
            var locked = await session.FindHeartbeatForUpdateAsync(hb.HeartbeatId, ct);
            if (locked is null) return null;
            var now = time.GetUtcNow();
            var startedAt = locked.RunStartedAt;
            var transition = HeartbeatMachine.OnPing(locked, runKind, exitCode, now);
            locked.LastPingIp = sourceIp;
            if (!transition.Changed) locked.UpdatedAt = now;
            session.AddHeartbeatRun(new HeartbeatRun
            {
                HeartbeatId = locked.HeartbeatId,
                Seq = await session.NextHeartbeatRunSeqAsync(locked.HeartbeatId, ct),
                StartedAt = runKind == RunKinds.Start ? now : startedAt,
                FinishedAt = now,
                Kind = runKind,
                ExitCode = exitCode,
                Body = stored,
                SourceIp = sourceIp,
            });
            await session.TrimHeartbeatRunsAsync(locked.HeartbeatId, options.Value.RunRingSize, ct);
            var detail = runKind switch
            {
                RunKinds.Fail => "the job reported failure explicitly (/fail)",
                RunKinds.Exit when exitCode is not 0 => $"the job exited with code {exitCode}",
                _ => null,
            };
            bool? pingSuccess = runKind == RunKinds.Start ? null : !(runKind == RunKinds.Fail || (runKind == RunKinds.Exit && exitCode is not 0));
            var episodes = await effects.ApplyAsync(locked, transition, session, now, detail, ct, pingSuccess);
            return new HeartbeatOutcome(locked, transition, episodes);
        }, ct);
        // Every accepted ping is announced: the detail screen shows last ping and run history live, not only state changes (08 §3.4).
        if (outcome is not null) await effects.AnnounceAsync(outcome.Heartbeat, outcome.Episodes, ct);
        return outcome is not null;
    }

    /// <summary>The last few lines of a failing job are usually the whole diagnosis: keep the tail (06 §3).</summary>
    public static string? Tail(string? body, int maxBytes)
    {
        if (string.IsNullOrEmpty(body)) return null;
        var bytes = Encoding.UTF8.GetByteCount(body);
        if (bytes <= maxBytes) return body;
        var utf8 = Encoding.UTF8.GetBytes(body);
        var start = utf8.Length - maxBytes;
        while (start < utf8.Length && (utf8[start] & 0xC0) == 0x80) start++; // do not split a multi-byte sequence
        return "…" + Encoding.UTF8.GetString(utf8, start, utf8.Length - start);
    }
}

/// <summary>
/// The scheduler side (04 §10, spec §13.3.3 "missing is detected by the scheduler, not by the ping"): every 30 s, due heartbeats
/// move <c>healthy → late → missed</c>; maintenance windows pause and resume heartbeats whose scope they cover.
/// </summary>
public sealed class HeartbeatMonitor(IProcessingUnitOfWork uow, HeartbeatEffects effects, IMaintenanceWindows maintenance, TimeProvider time, ILogger<HeartbeatMonitor> logger)
{
    public async Task<IReadOnlyList<HeartbeatOutcome>> TickAsync(CancellationToken ct, int limit = 200)
    {
        var changed = new List<HeartbeatOutcome>();
        changed.AddRange(await uow.RunAsync(async session =>
        {
            var now = time.GetUtcNow();
            var outcomes = new List<HeartbeatOutcome>();
            foreach (var hb in await session.ClaimDueHeartbeatsAsync(now, limit, ct))
            {
                var transition = HeartbeatMachine.OnTick(hb, now);
                if (!transition.Changed) continue;
                var episodes = await effects.ApplyAsync(hb, transition, session, now, transition.Missed ? $"no ping since {hb.LastPingAt?.ToString("u") ?? "registration"}; expected by {hb.ExpectedNext:u}, grace {hb.Grace}" : null, ct);
                outcomes.Add(new HeartbeatOutcome(hb, transition, episodes));
            }
            return outcomes;
        }, ct));
        changed.AddRange(await MaintenanceSweepAsync(ct));
        foreach (var outcome in changed) await effects.AnnounceAsync(outcome.Heartbeat, outcome.Episodes, ct);
        if (changed.Count > 0) logger.LogInformation("heartbeat_check: {Count} transition(s)", changed.Count);
        return changed;
    }

    /// <summary>Auto-pause while a maintenance window covers the scope, resume when it ends — without a false miss (spec §22).</summary>
    private async Task<IReadOnlyList<HeartbeatOutcome>> MaintenanceSweepAsync(CancellationToken ct)
        => await uow.RunAsync(async session =>
        {
            var now = time.GetUtcNow();
            var outcomes = new List<HeartbeatOutcome>();
            foreach (var hb in await session.ListMaintenanceCandidatesForUpdateAsync(ct))
            {
                var covered = await maintenance.CoversAsync(hb.AccessScope, now, ct);
                HeartbeatTransition transition;
                if (covered && !hb.IsPaused) transition = HeartbeatMachine.Pause(hb, null, "maintenance window", now, byMaintenance: true);
                else if (!covered && hb.IsPaused && hb.PausedByMaintenance) transition = HeartbeatMachine.Resume(hb, now);
                else continue;
                if (!transition.Changed) continue;
                var episodes = await effects.ApplyAsync(hb, transition, session, now, covered ? "maintenance window began" : "maintenance window ended", ct);
                outcomes.Add(new HeartbeatOutcome(hb, transition, episodes));
            }
            return outcomes;
        }, ct);
}
