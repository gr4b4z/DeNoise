using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlertHub.Application.Abstractions;
using AlertHub.Domain.Common;
using AlertHub.Domain.Episodes;
using AlertHub.Domain.Notifications;
using AlertHub.Domain.Ops;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlertHub.Application.Notifications;

/// <summary>Outbox claim/complete with the same reservation semantics as the job queue.</summary>
public interface IOutboxQueue
{
    Task<IReadOnlyList<OutboxMessage>> ClaimAsync(string workerId, TimeSpan lease, int limit, CancellationToken ct = default);
    Task<bool> MarkSentAsync(Guid outboxId, string workerId, CancellationToken ct = default);
    Task<bool> RescheduleAsync(Guid outboxId, string workerId, DateTimeOffset notBefore, string error, CancellationToken ct = default);
    Task<bool> MarkFailedAsync(Guid outboxId, string workerId, string error, CancellationToken ct = default);
    Task<bool> MarkCoalescedAsync(Guid outboxId, string workerId, string reason, CancellationToken ct = default);
    /// <summary>True when a later terminal row (<c>episode.closed</c>) exists for the episode, making earlier state notifications obsolete.</summary>
    Task<bool> HasLaterTerminalAsync(Guid episodeId, DateTimeOffset createdAfter, CancellationToken ct = default);
    /// <summary>Immediately committed insert (fallback rows and hub notifications created by the dispatcher itself).</summary>
    Task EnqueueAsync(OutboxMessage message, CancellationToken ct = default);
    Task RecordAttemptAsync(DeliveryAttempt attempt, CancellationToken ct = default);
    Task UpdateDestinationHealthAsync(Guid destinationId, bool success, DateTimeOffset at, CancellationToken ct = default);
    /// <summary>Count of suppressed rows released to pending (suppression end, milestone 9) — placeholder for the reaper contract.</summary>
    Task<int> ReleaseSuppressedAsync(Guid episodeId, CancellationToken ct = default);
}

public interface IEpisodeReader
{
    Task<Episode?> GetAsync(Guid episodeId, CancellationToken ct = default);
}

/// <summary>Per-message dispatch outcome, for tests and logging.</summary>
public enum DispatchOutcome
{
    Sent,
    Rescheduled,
    Failed,
    FailedWithFallback,
    Coalesced,
    ReservationLost,
}

/// <summary>
/// Dispatcher (03 §2 "Dispatch"): claim → relevance check → render → send → record attempt → transition the outbox row.
/// Response classification and the fixed backoff come from ADR-7; a permanent failure or exhausted attempts route the
/// notification to the destination's mandatory fallback once and raise <c>hub.delivery_failure</c>.
/// </summary>
public sealed class OutboxDispatcher(
    IOutboxQueue outbox, IEpisodeReader episodes, IDestinationRepository destinations, ISecretProtector protector,
    IEnumerable<INotificationChannel> channels, IOptions<NotificationOptions> options, TimeProvider time, ILogger<OutboxDispatcher> logger)
{
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private const string FallbackOfKey = "_fallbackOf";
    private readonly Dictionary<string, INotificationChannel> _channels = channels.ToDictionary(c => c.ChannelType, StringComparer.Ordinal);

    public async Task<IReadOnlyList<(OutboxMessage Message, DispatchOutcome Outcome)>> DispatchBatchAsync(string workerId, int limit, CancellationToken ct)
    {
        var batch = await outbox.ClaimAsync(workerId, Lease, limit, ct);
        var results = new List<(OutboxMessage, DispatchOutcome)>(batch.Count);
        foreach (var message in batch)
        {
            results.Add((message, await DispatchOneAsync(message, workerId, ct)));
        }
        return results;
    }

    public async Task<DispatchOutcome> DispatchOneAsync(OutboxMessage message, string workerId, CancellationToken ct)
    {
        var now = time.GetUtcNow();

        // Relevance (04 §6): an old state notification must never arrive after recovery as if current.
        if (message.EpisodeId is { } episodeId && NotificationTypes.IsSupersededByClosure(message.Type))
        {
            var episode = await episodes.GetAsync(episodeId, ct);
            var obsolete = episode is null || !episode.IsOpen
                || (message.Type == NotificationTypes.EpisodeAckOverdue && episode.HandlingState != HandlingState.New)
                || await outbox.HasLaterTerminalAsync(episodeId, message.CreatedAt, ct);
            if (obsolete)
            {
                return await outbox.MarkCoalescedAsync(message.OutboxId, workerId, "superseded by a later transition", ct) ? DispatchOutcome.Coalesced : DispatchOutcome.ReservationLost;
            }
        }

        var destination = await destinations.GetAsync(message.DestinationId, ct);
        if (destination is null || !destination.Active)
        {
            return await FailAsync(message, destination, workerId, destination is null ? "destination not found" : "destination inactive", now, permanent: true, ct);
        }
        if (!_channels.TryGetValue(destination.ChannelType, out var channel))
        {
            return await FailAsync(message, destination, workerId, $"no channel for '{destination.ChannelType}'", now, permanent: true, ct);
        }

        var body = Render(message, now);
        var resolved = Resolve(destination);
        var started = Stopwatch.GetTimestamp();
        ChannelResult result;
        try
        {
            result = await channel.SendAsync(resolved, message, body, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = ChannelResult.Retryable(ex.GetType().Name + ": " + ex.Message);
        }
        var latency = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        await outbox.RecordAttemptAsync(new DeliveryAttempt
        {
            Id = Ids.New(time),
            OutboxId = message.OutboxId,
            AttemptedAt = now,
            Channel = destination.ChannelType,
            Outcome = result.Outcome,
            HttpStatus = result.HttpStatus,
            LatencyMs = latency,
            Error = result.Error,
            UsedFallback = IsFallback(message),
            ResponseExcerpt = result.ResponseExcerpt,
        }, ct);
        await outbox.UpdateDestinationHealthAsync(destination.DestinationId, result.Outcome == DeliveryOutcomes.Success, now, ct);

        switch (result.Outcome)
        {
            case DeliveryOutcomes.Success:
                return await outbox.MarkSentAsync(message.OutboxId, workerId, ct) ? DispatchOutcome.Sent : DispatchOutcome.ReservationLost;
            case DeliveryOutcomes.Permanent:
                return await FailAsync(message, destination, workerId, result.Error ?? "permanent failure", now, permanent: true, ct);
            default: // retryable or response_lost: same delivery id on every retry (X-AlertHub-Delivery-Id = outbox id)
                if (message.Attempts >= MaxAttempts(destination))
                {
                    return await FailAsync(message, destination, workerId, $"{result.Error} (attempts exhausted)", now, permanent: false, ct);
                }
                var delay = DeliveryBackoff.For(message.Attempts, result.RetryAfter);
                return await outbox.RescheduleAsync(message.OutboxId, workerId, now + delay, result.Error ?? result.Outcome, ct) ? DispatchOutcome.Rescheduled : DispatchOutcome.ReservationLost;
        }
    }

    private int MaxAttempts(Destination destination)
    {
        if (destination.RetryPolicy is not null)
        {
            try
            {
                if (JsonNode.Parse(destination.RetryPolicy) is JsonObject o && o["maxAttempts"] is JsonValue v && v.TryGetValue<int>(out var m) && m > 0) return m;
            }
            catch (JsonException)
            {
                // fall through to default
            }
        }
        return options.Value.MaxAttempts;
    }

    private async Task<DispatchOutcome> FailAsync(OutboxMessage message, Destination? destination, string workerId, string error, DateTimeOffset now, bool permanent, CancellationToken ct)
    {
        if (!await outbox.MarkFailedAsync(message.OutboxId, workerId, error, ct)) return DispatchOutcome.ReservationLost;
        logger.LogWarning("Outbox {OutboxId} ({Type}) to destination {DestinationId} failed {Mode}: {Error}", message.OutboxId, message.Type, message.DestinationId, permanent ? "permanently" : "after exhausting retries", error);

        if (destination is null || IsFallback(message) || destination.FallbackDestinationId == destination.DestinationId)
        {
            return DispatchOutcome.Failed; // one hop only; a fallback never falls back again
        }
        var fallback = await destinations.GetAsync(destination.FallbackDestinationId, ct);
        if (fallback is null || !fallback.Active)
        {
            return DispatchOutcome.Failed;
        }

        var payload = JsonNode.Parse(message.Payload) as JsonObject ?? new JsonObject();
        payload[FallbackOfKey] = message.OutboxId.ToString();
        await outbox.EnqueueAsync(new OutboxMessage
        {
            OutboxId = Ids.New(time),
            EpisodeId = message.EpisodeId,
            Type = message.Type,
            DestinationId = fallback.DestinationId,
            PolicyVersion = message.PolicyVersion,
            Payload = payload.ToJsonString(JsonDefaults.Stored),
            Status = OutboxStatus.Pending,
            NotBefore = now,
            CreatedAt = now,
        }, ct);

        if (fallback.Subscribes(NotificationTypes.HubDeliveryFailure))
        {
            var failureModel = NotificationModel.ForHub(NotificationTypes.HubDeliveryFailure, new
            {
                destinationId = destination.DestinationId,
                destinationName = destination.Name,
                originalOutboxId = message.OutboxId,
                originalType = message.Type,
                episodeId = message.EpisodeId,
                error,
                permanent,
                fallbackDestinationId = fallback.DestinationId,
            });
            failureModel[FallbackOfKey] = message.OutboxId.ToString();
            await outbox.EnqueueAsync(new OutboxMessage
            {
                OutboxId = Ids.New(time),
                EpisodeId = message.EpisodeId,
                Type = NotificationTypes.HubDeliveryFailure,
                DestinationId = fallback.DestinationId,
                Payload = failureModel.ToJsonString(JsonDefaults.Stored),
                Status = OutboxStatus.Pending,
                NotBefore = now,
                CreatedAt = now,
            }, ct);
        }
        return DispatchOutcome.FailedWithFallback;
    }

    private static bool IsFallback(OutboxMessage message)
    {
        try
        {
            return JsonNode.Parse(message.Payload) is JsonObject o && o[FallbackOfKey] is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The <c>generic-json</c> body: the notification model with delivery id and send time filled in. Templates arrive with milestone 7.</summary>
    public static string Render(OutboxMessage message, DateTimeOffset sentAt)
    {
        JsonObject model;
        try
        {
            model = JsonNode.Parse(message.Payload) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            model = new JsonObject { ["raw"] = message.Payload };
        }
        model["event"] = message.Type;
        model["deliveryId"] = message.OutboxId.ToString();
        model["sentAt"] = sentAt.ToString("O");
        model.Remove(FallbackOfKey);
        return model.ToJsonString(JsonDefaults.Stored);
    }

    private ResolvedDestination Resolve(Destination destination)
    {
        var url = destination.UrlEnc is null ? null : protector.Unprotect(destination.UrlEnc);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (destination.HeadersEnc is not null)
        {
            try
            {
                foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, string>>(protector.Unprotect(destination.HeadersEnc), JsonDefaults.Stored) ?? [])
                {
                    headers[k] = v;
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Destination {DestinationId} has unreadable headers; sending without them", destination.DestinationId);
            }
        }
        var secret = destination.SigningSecretEnc is null ? null : protector.Unprotect(destination.SigningSecretEnc);
        return new ResolvedDestination(destination, url, headers, secret);
    }
}
