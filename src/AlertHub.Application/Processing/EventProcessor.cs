using System.Text.Json;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Ingest;
using AlertHub.Application.Integrations;
using AlertHub.Application.Mapping;
using AlertHub.Domain.Alerts;
using AlertHub.Domain.Audit;
using AlertHub.Domain.Common;
using AlertHub.Domain.Episodes;
using AlertHub.Domain.Integrations;
using Microsoft.Extensions.Logging;

namespace AlertHub.Application.Processing;

public enum ProcessingOutcome
{
    RawEventGone,
    MappingFailed,
    Duplicate,
    Late,
    Replayed,
    CoverageSignal,
    RecordedWithoutEpisode,
    Opened,
    Updated,
    Resolved,
    Cancelled,
}

public sealed record ProcessingResult(ProcessingOutcome Outcome, Guid? EpisodeId = null, string? Detail = null);

/// <summary>
/// The <c>normalise</c> step (03 §2 "Process"): load raw → select mapping → normalise → delivery key + fingerprint →
/// one transaction: ledger insert, ordering, episode transition, timeline, audit, transition hook → commit → publish.
/// Idempotent by construction: re-running for the same raw event hits the ledger and stops.
/// </summary>
public sealed class EventProcessor(
    IRawEventReader rawEvents, IIntegrationRepository integrations, IMappingResolver mappings, IProcessingUnitOfWork uow,
    ITransitionHook transitionHook, IEpisodeChangePublisher publisher, TimeProvider time, ILogger<EventProcessor> logger)
{
    public const int MaxConflictRetries = 5;

    public async Task<ProcessingResult> ProcessAsync(NormaliseJobPayload payload, CancellationToken ct = default)
    {
        var raw = await rawEvents.GetAsync(payload.EventId, payload.ReceivedAt, ct);
        if (raw is null)
        {
            logger.LogWarning("Raw event {EventId} not found (retention or partition gap); nothing to process", payload.EventId);
            return new ProcessingResult(ProcessingOutcome.RawEventGone);
        }

        var integration = await integrations.GetCurrentAsync(raw.IntegrationId, ct);
        if (integration is null)
        {
            return await QuarantineAsync(raw, null, "integration configuration not found", null, ct);
        }

        var input = new RawInput(raw.Body, ParseHeaders(raw.Headers));
        MappingDocument? mapping;
        try
        {
            mapping = await SelectMappingAsync(integration, input, payload.MappingVersion, ct);
        }
        catch (MappingException ex)
        {
            return await QuarantineAsync(raw, null, ex.Message, ex.Field, ct);
        }
        if (mapping is null)
        {
            return await QuarantineAsync(raw, null, "no active mapping applies to this event", null, ct);
        }

        MappingResult mapped;
        try
        {
            mapped = MappingEngine.Normalise(mapping, input, new MappingContext(integration.IntegrationId, raw.EventId, raw.ReceivedAt, mapping.Version));
        }
        catch (MappingException ex)
        {
            return await QuarantineAsync(raw, mapping.Version, ex.Message, ex.Field, ct);
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var (result, changed) = await uow.RunAsync(session => ApplyAsync(session, integration, mapped.Event, payload.IsReplay, ct), ct);
                if (changed is not null)
                {
                    try
                    {
                        await publisher.PublishAsync(changed, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogDebug(ex, "Post-commit publish failed for episode {EpisodeId}; the UI will resync", changed.EpisodeId);
                    }
                }
                return result;
            }
            catch (ProcessingConflictException ex) when (attempt < MaxConflictRetries)
            {
                logger.LogInformation(ex, "Processing conflict for event {EventId} (attempt {Attempt}); retrying", raw.EventId, attempt);
            }
        }
    }

    private async Task<(ProcessingResult Result, Episode? Changed)> ApplyAsync(IProcessingSession session, Integration integration, NormalisedEvent evt, bool isReplay, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var correlation = evt.EventId.ToString("N");
        var actor = Actor.Integration(integration.IntegrationId, correlation);

        // Replay never transitions (04 §5): it only leaves a trace on whatever episode the event belongs to.
        if (isReplay)
        {
            var existing = await session.FindEpisodeIdForDeliveryKeyAsync(integration.IntegrationId, evt.DeliveryKey, ct);
            if (existing is null)
            {
                var recorded = await session.TryRecordAppliedAsync(Applied(evt, now, AppliedOutcomes.Replayed), ct);
                if (recorded) session.AddNormalisedEvent(evt);
                return (new ProcessingResult(ProcessingOutcome.Replayed), null);
            }
            session.AddEpisodeEvent(Timeline(existing.Value, evt, now, EpisodeEventKind.Replayed, new { reason = "historical replay; no transition", mappingVersion = evt.MappingVersion }));
            return (new ProcessingResult(ProcessingOutcome.Replayed, existing), null);
        }

        // Heartbeat / canary: coverage only, never an episode (04 §2.1).
        if (evt.EventType == EventTypes.Heartbeat)
        {
            if (!await session.TryRecordAppliedAsync(Applied(evt, now, AppliedOutcomes.Coverage), ct))
            {
                await session.RecordDuplicateAsync(integration.IntegrationId, evt.DeliveryKey, now, ct);
                return (new ProcessingResult(ProcessingOutcome.Duplicate), null);
            }
            session.AddNormalisedEvent(evt);
            return (new ProcessingResult(ProcessingOutcome.CoverageSignal), null);
        }

        // Events without identity (informational lacking identity fields) are recorded but form no episode.
        if (evt.Fingerprint is null)
        {
            if (!await session.TryRecordAppliedAsync(Applied(evt, now, AppliedOutcomes.Applied), ct))
            {
                await session.RecordDuplicateAsync(integration.IntegrationId, evt.DeliveryKey, now, ct);
                return (new ProcessingResult(ProcessingOutcome.Duplicate), null);
            }
            session.AddNormalisedEvent(evt);
            return (new ProcessingResult(ProcessingOutcome.RecordedWithoutEpisode), null);
        }

        var open = await session.FindOpenEpisodeForUpdateAsync(evt.Fingerprint, ct);

        if (open is not null)
        {
            if (!Ordering.IsEffective(open, evt))
            {
                return (await RecordLateAsync(session, integration, evt, open, now, "older than the episode's latest effective signal", ct), null);
            }
            if (!await session.TryRecordAppliedAsync(Applied(evt, now, AppliedOutcomes.Applied), ct))
            {
                await session.RecordDuplicateAsync(integration.IntegrationId, evt.DeliveryKey, now, ct);
                return (new ProcessingResult(ProcessingOutcome.Duplicate, open.EpisodeId), null);
            }
            evt.EpisodeId = open.EpisodeId;
            session.AddNormalisedEvent(evt);

            switch (evt.EventType)
            {
                case EventTypes.Resolved:
                    {
                        var t = open.ApplySourceResolved(evt, now);
                        session.AddEpisodeEvent(Timeline(open.EpisodeId, evt, now, EpisodeEventKind.SourceEvent, new { eventType = evt.EventType, transition = "resolved" }));
                        session.AddAudit(AuditFor(actor, "episode.resolve", open, now, new { by = "source", evidence = Evidence.Source }));
                        await transitionHook.OnTransitionAsync(open, evt, t, session, ct);
                        return (new ProcessingResult(ProcessingOutcome.Resolved, open.EpisodeId), open);
                    }
                case EventTypes.Cancelled:
                    {
                        var t = open.ApplySourceCancelled(evt, now);
                        session.AddEpisodeEvent(Timeline(open.EpisodeId, evt, now, EpisodeEventKind.SourceEvent, new { eventType = evt.EventType, transition = "cancelled" }));
                        session.AddAudit(AuditFor(actor, "episode.cancel", open, now, new { by = "source" }));
                        await transitionHook.OnTransitionAsync(open, evt, t, session, ct);
                        return (new ProcessingResult(ProcessingOutcome.Cancelled, open.EpisodeId), open);
                    }
                case EventTypes.Acknowledged:
                    {
                        // A producer-side acknowledgement is a user response, not a condition change (spec §15.3): record only.
                        open.Touch(now);
                        session.AddEpisodeEvent(Timeline(open.EpisodeId, evt, now, EpisodeEventKind.SourceEvent, new { eventType = evt.EventType, note = "source acknowledgement recorded; handling state unchanged" }));
                        return (new ProcessingResult(ProcessingOutcome.Updated, open.EpisodeId), open);
                    }
                default:
                    {
                        var t = open.ApplySignal(evt, now);
                        session.AddEpisodeEvent(Timeline(open.EpisodeId, evt, now, EpisodeEventKind.SourceEvent,
                            new { eventType = evt.EventType, severity = evt.Severity.ToWire(), previousSeverity = t.PreviousSeverity.ToWire(), occurrenceCount = open.OccurrenceCount }));
                        if (t.IsMaterialSeverityIncrease)
                        {
                            session.AddAudit(AuditFor(actor, "episode.severity_increase", open, now, new { from = t.PreviousSeverity.ToWire(), to = t.NewSeverity.ToWire() }));
                        }
                        await transitionHook.OnTransitionAsync(open, evt, t, session, ct);
                        return (new ProcessingResult(ProcessingOutcome.Updated, open.EpisodeId), open);
                    }
            }
        }

        // No open episode for this identity.
        var latestClosed = await session.FindLatestClosedEpisodeAsync(evt.Fingerprint, ct);

        if (evt.EventType is EventTypes.Resolved or EventTypes.Cancelled or EventTypes.Acknowledged)
        {
            // Recovery before opening (04 §5): remember the closed source instance so a delayed opening cannot reactivate it.
            if (!await session.TryRecordAppliedAsync(Applied(evt, now, AppliedOutcomes.Applied), ct))
            {
                await session.RecordDuplicateAsync(integration.IntegrationId, evt.DeliveryKey, now, ct);
                return (new ProcessingResult(ProcessingOutcome.Duplicate), null);
            }
            if (evt.EventType is EventTypes.Resolved or EventTypes.Cancelled && evt.SourceAlertId is not null)
            {
                await session.UpsertSourceInstanceStateAsync(integration.IntegrationId, evt.SourceAlertId, evt.EffectiveAt, ct);
            }
            if (latestClosed is not null)
            {
                evt.EpisodeId = latestClosed.EpisodeId;
                session.AddEpisodeEvent(Timeline(latestClosed.EpisodeId, evt, now, EpisodeEventKind.LateEvent, new { eventType = evt.EventType, reason = "arrived after the episode closed; no state change" }));
            }
            session.AddNormalisedEvent(evt);
            return (new ProcessingResult(ProcessingOutcome.RecordedWithoutEpisode, latestClosed?.EpisodeId, "recovery with no open episode"), null);
        }

        // firing / update / informational with no open episode.
        if (evt.SourceAlertId is not null)
        {
            var marker = await session.GetSourceInstanceStateAsync(integration.IntegrationId, evt.SourceAlertId, ct);
            if (marker is not null && evt.EffectiveAt <= marker.ResolvedAt)
            {
                return (await RecordLateAsync(session, integration, evt, latestClosed, now, $"source instance already resolved at {marker.ResolvedAt:O}", ct), null);
            }
        }
        if (latestClosed is not null && evt.OccurredAt is not null && evt.OccurredAt <= latestClosed.LastSeen)
        {
            return (await RecordLateAsync(session, integration, evt, latestClosed, now, "older than the previous episode's latest signal", ct), null);
        }

        if (!await session.TryRecordAppliedAsync(Applied(evt, now, AppliedOutcomes.Applied), ct))
        {
            await session.RecordDuplicateAsync(integration.IntegrationId, evt.DeliveryKey, now, ct);
            return (new ProcessingResult(ProcessingOutcome.Duplicate), null);
        }

        await session.EnsureIdentityAsync(new AlertIdentity
        {
            Fingerprint = evt.Fingerprint,
            IntegrationId = integration.IntegrationId,
            AccessScope = integration.AccessScope,
            IdentityVersion = 0,
            Components = JsonSerializer.Serialize(evt.IdentityComponents ?? [], JsonDefaults.Stored),
            FirstSeen = evt.EffectiveAt,
        }, ct);

        var episode = Episode.Open(evt, integration.AccessScope, now, time, latestClosed?.EpisodeId);
        episode.OwningTeamId = integration.OwnerTeamId; // routing (milestone 3) refines this inside the hook
        session.AddEpisode(episode);
        await session.IncrementIdentityEpisodeCountAsync(evt.Fingerprint, ct);
        evt.EpisodeId = episode.EpisodeId;
        session.AddNormalisedEvent(evt);
        session.AddEpisodeEvent(Timeline(episode.EpisodeId, evt, now, EpisodeEventKind.SourceEvent,
            new { eventType = evt.EventType, severity = evt.Severity.ToWire(), transition = "opened", previousEpisodeId = latestClosed?.EpisodeId }));
        session.AddAudit(AuditFor(actor, "episode.open", episode, now, new { severity = episode.Severity.ToWire(), condition = episode.ConditionState, previousEpisodeId = latestClosed?.EpisodeId }));
        await transitionHook.OnTransitionAsync(episode, evt, new EpisodeTransition(EpisodeTransitionKind.Opened, ConditionState.Firing, episode.ConditionState, episode.Severity, episode.Severity), session, ct);
        return (new ProcessingResult(ProcessingOutcome.Opened, episode.EpisodeId), episode);
    }

    private static async Task<ProcessingResult> RecordLateAsync(IProcessingSession session, Integration integration, NormalisedEvent evt, Episode? episode, DateTimeOffset now, string why, CancellationToken ct)
    {
        if (!await session.TryRecordAppliedAsync(Applied(evt, now, AppliedOutcomes.Late), ct))
        {
            await session.RecordDuplicateAsync(integration.IntegrationId, evt.DeliveryKey, now, ct);
            return new ProcessingResult(ProcessingOutcome.Duplicate, episode?.EpisodeId);
        }
        if (episode is not null)
        {
            evt.EpisodeId = episode.EpisodeId;
            session.AddEpisodeEvent(Timeline(episode.EpisodeId, evt, now, EpisodeEventKind.LateEvent, new { eventType = evt.EventType, occurredAt = evt.OccurredAt, reason = why }));
        }
        session.AddNormalisedEvent(evt);
        return new ProcessingResult(ProcessingOutcome.Late, episode?.EpisodeId, why);
    }

    private async Task<ProcessingResult> QuarantineAsync(RawEvent raw, int? mappingVersion, string error, string? field, CancellationToken ct)
    {
        logger.LogWarning("Mapping failure for event {EventId} of integration {IntegrationId}: {Error} (field {Field})", raw.EventId, raw.IntegrationId, error, field);
        await uow.RunAsync<object?>(session =>
        {
            session.AddMappingFailure(new MappingFailure
            {
                Id = Ids.New(time),
                IntegrationId = raw.IntegrationId,
                EventId = raw.EventId,
                RawReceivedAt = raw.ReceivedAt,
                MappingVersion = mappingVersion,
                Error = error.Length > 2000 ? error[..2000] : error,
                Field = field,
            });
            return Task.FromResult<object?>(null);
        }, ct);
        return new ProcessingResult(ProcessingOutcome.MappingFailed, Detail: error);
    }

    private async Task<MappingDocument?> SelectMappingAsync(Integration integration, RawInput input, int? forcedVersion, CancellationToken ct)
    {
        var active = await mappings.GetActiveAsync(integration.IntegrationId, ct);
        if (forcedVersion is { } v)
        {
            return active.FirstOrDefault(m => m.Version == v) ?? throw new MappingException($"mapping version {v} is not active");
        }
        if (active.Count == 0)
        {
            return integration.Type == IntegrationTypes.GenericWebhook ? BuiltInMappings.GenericWebhook : null;
        }
        var body = input.ParseBody();
        var headers = input.HeadersAsJson();
        return active.FirstOrDefault(m => MappingEngine.AppliesTo(m, body, headers));
    }

    private static IReadOnlyDictionary<string, string> ParseHeaders(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new Dictionary<string, string>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonDefaults.Stored) ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    private static AppliedEvent Applied(NormalisedEvent evt, DateTimeOffset now, string outcome) => new()
    {
        IntegrationId = evt.IntegrationId,
        DeliveryKey = evt.DeliveryKey,
        EventId = evt.EventId,
        AppliedAt = now,
        Outcome = outcome,
    };

    private static EpisodeEvent Timeline(Guid episodeId, NormalisedEvent evt, DateTimeOffset now, string kind, object detail) => new()
    {
        Id = Guid.CreateVersion7(now),
        EpisodeId = episodeId,
        At = now,
        Kind = kind,
        EventId = evt.EventId,
        Detail = JsonSerializer.Serialize(detail, JsonDefaults.Stored),
    };

    private static AuditEntry AuditFor(Actor actor, string action, Episode episode, DateTimeOffset now, object after) => new()
    {
        Id = Guid.CreateVersion7(now),
        At = now,
        ActorType = actor.Type,
        ActorId = actor.Id,
        ActorDisplay = actor.Display,
        Action = action,
        TargetType = "episode",
        TargetId = episode.EpisodeId.ToString(),
        AccessScope = episode.AccessScope,
        After = JsonSerializer.Serialize(after, JsonDefaults.Stored),
        CorrelationId = actor.CorrelationId,
    };
}
