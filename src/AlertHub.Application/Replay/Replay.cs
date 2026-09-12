using System.Text.Json;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Audit;
using AlertHub.Application.Ingest;
using AlertHub.Application.Integrations;
using AlertHub.Application.Ops;
using AlertHub.Application.Processing;
using AlertHub.Domain.Alerts;
using AlertHub.Domain.Audit;
using AlertHub.Domain.Common;
using AlertHub.Domain.Ops;
using Microsoft.Extensions.Logging;

namespace AlertHub.Application.Replay;

/// <summary>Replay modes of spec §17.5. <c>preview</c> is synchronous (mapping preview endpoint) and never becomes a job.</summary>
public static class ReplayModes
{
    public const string RetryFailed = "retry_failed";
    public const string Historical = "historical";
    public static readonly IReadOnlyList<string> Jobs = [RetryFailed, Historical];
}

/// <summary>Stored in <c>ops.job.payload</c>: the selection is part of the record (spec §17.5 "replay records actor, selection, mapping version and outcome").</summary>
public sealed record ReplayJobPayload(string Mode, Guid IntegrationId, Guid[]? EventIds, DateTimeOffset? From, DateTimeOffset? To, int? MappingVersion, string ActorId, int Limit);

/// <summary>Stored in <c>ops.job.result</c> when the job finishes.</summary>
public sealed record ReplayResult(int Selected, int Processed, IReadOnlyDictionary<string, int> Outcomes, int FailuresResolved, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, bool Truncated);

public sealed record StartReplay(string Mode, Guid IntegrationId, IReadOnlyList<Guid>? EventIds = null, DateTimeOffset? From = null, DateTimeOffset? To = null, int? MappingVersion = null, int Limit = 5000);

/// <summary>Quarantined events (<c>alert.mapping_failure</c>) as the replay and the failures screen see them.</summary>
public interface IMappingFailureStore
{
    Task<IReadOnlyList<MappingFailure>> ListAsync(Guid integrationId, bool quarantinedOnly, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<MappingFailure>> GetByEventsAsync(Guid integrationId, IReadOnlyCollection<Guid> eventIds, CancellationToken ct = default);
    /// <summary>Marks the failures of these events resolved (a replay interpreted them). Returns rows changed.</summary>
    Task<int> ResolveAsync(IReadOnlyCollection<Guid> eventIds, DateTimeOffset at, Guid? by, CancellationToken ct = default);
    /// <summary>Takes one failure out of the queue without replaying it (operator decision, audited by the caller).</summary>
    Task<bool> DismissAsync(Guid failureId, DateTimeOffset at, Guid? by, CancellationToken ct = default);
}

public sealed class ReplayService(IJobQueue jobs, IIntegrationRepository integrations, IAuditWriter audit, IUnitOfWork uow, TimeProvider time)
{
    public static readonly TimeSpan MaxHistoricalWindow = TimeSpan.FromDays(31);

    public async Task<Job> StartAsync(StartReplay request, Actor actor, CancellationToken ct = default)
    {
        if (!ReplayModes.Jobs.Contains(request.Mode)) throw new ArgumentException($"mode must be one of {string.Join(", ", ReplayModes.Jobs)} (preview is synchronous: use the mapping preview)", nameof(request));
        var integration = await integrations.GetCurrentAsync(request.IntegrationId, ct) ?? throw new KeyNotFoundException($"Integration {request.IntegrationId} not found.");
        var now = time.GetUtcNow();
        if (request.Mode == ReplayModes.Historical)
        {
            if (request.From is null || request.To is null) throw new ArgumentException("historical replay needs from and to", nameof(request));
            if (request.To <= request.From) throw new ArgumentException("to must be after from", nameof(request));
            if (request.To - request.From > MaxHistoricalWindow) throw new ArgumentException($"historical window is at most {MaxHistoricalWindow.TotalDays:F0} days", nameof(request));
        }
        var payload = new ReplayJobPayload(request.Mode, integration.IntegrationId, request.EventIds?.ToArray(), request.From, request.To, request.MappingVersion, actor.Id, Math.Clamp(request.Limit, 1, 50_000));
        var job = new Job
        {
            JobId = Ids.New(time),
            Kind = JobKinds.Replay,
            NotBefore = now,
            Priority = 200,
            Payload = JsonSerializer.Serialize(payload, JsonDefaults.Stored),
            IntegrationId = integration.IntegrationId,
            MaxAttempts = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        jobs.Enqueue(job);
        audit.Record(new AuditEntry
        {
            Id = Ids.New(time),
            At = now,
            ActorType = actor.Type,
            ActorId = actor.Id,
            ActorDisplay = actor.Display,
            Action = "replay.start",
            TargetType = "integration",
            TargetId = integration.IntegrationId.ToString(),
            AccessScope = integration.AccessScope,
            After = JsonSerializer.Serialize(new { jobId = job.JobId, payload.Mode, events = payload.EventIds?.Length, payload.From, payload.To, payload.MappingVersion }, JsonDefaults.Stored),
            CorrelationId = actor.CorrelationId,
            RequestIp = actor.Ip,
        });
        await uow.CommitAsync(ct);
        return job;
    }
}

/// <summary>
/// Runs a replay job (processing role). <c>retry_failed</c> re-interprets quarantined events live (they never reached the
/// ledger, so this is their first application); <c>historical</c> re-runs stored raw events with <c>IsReplay</c>, which
/// records without transitions, timers or notifications (04 §5). Progress extends the lease; the summary lands in <c>result</c>.
/// </summary>
public sealed class ReplayJobHandler(EventProcessor processor, IMappingFailureStore failures, IRawEventReader rawEvents, IJobQueue jobs, TimeProvider time, ILogger<ReplayJobHandler> logger) : IJobHandler
{
    public const int LeaseEvery = 25;
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    public string Kind => JobKinds.Replay;

    public async Task HandleAsync(Job job, JobContext context, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<ReplayJobPayload>(job.Payload, JsonDefaults.Stored) ?? throw new InvalidOperationException($"job {job.JobId} has no payload");
        var started = time.GetUtcNow();
        var outcomes = new Dictionary<string, int>(StringComparer.Ordinal);
        var resolved = new List<Guid>();
        IReadOnlyList<(Guid EventId, DateTimeOffset ReceivedAt)> selection;
        bool truncated;
        if (payload.Mode == ReplayModes.RetryFailed)
        {
            var rows = payload.EventIds is { Length: > 0 } ids
                ? await failures.GetByEventsAsync(payload.IntegrationId, ids, ct)
                : await failures.ListAsync(payload.IntegrationId, quarantinedOnly: true, payload.Limit + 1, ct);
            truncated = rows.Count > payload.Limit;
            selection = rows.Take(payload.Limit).Select(f => (f.EventId, f.RawReceivedAt)).Distinct().ToList();
        }
        else
        {
            var rows = await rawEvents.ListAsync(payload.IntegrationId, payload.From!.Value, payload.To!.Value, payload.Limit + 1, ct);
            truncated = rows.Count > payload.Limit;
            selection = rows.Take(payload.Limit).ToList();
        }

        var processed = 0;
        foreach (var (eventId, receivedAt) in selection)
        {
            ct.ThrowIfCancellationRequested();
            var result = await processor.ProcessAsync(new NormaliseJobPayload(eventId, receivedAt, payload.IntegrationId, IsReplay: payload.Mode == ReplayModes.Historical, MappingVersion: payload.MappingVersion), ct);
            var key = result.Outcome.ToString();
            outcomes[key] = outcomes.GetValueOrDefault(key) + 1;
            if (payload.Mode == ReplayModes.RetryFailed && result.Outcome is not ProcessingOutcome.MappingFailed and not ProcessingOutcome.RawEventGone) resolved.Add(eventId);
            processed++;
            if (processed % LeaseEvery == 0 && !await context.ExtendLeaseAsync(Lease, ct))
            {
                logger.LogWarning("Replay {JobId} lost its reservation after {Processed} events; stopping", job.JobId, processed);
                context.Retained = true;
                return;
            }
        }
        var resolvedCount = resolved.Count == 0 ? 0 : await failures.ResolveAsync(resolved, time.GetUtcNow(), Guid.TryParse(payload.ActorId, out var by) ? by : null, ct);
        var summary = new ReplayResult(selection.Count, processed, outcomes, resolvedCount, started, time.GetUtcNow(), truncated);
        await jobs.RecordResultAsync(job.JobId, context.WorkerId, JsonSerializer.Serialize(summary, JsonDefaults.Stored), ct);
        logger.LogInformation("Replay {JobId} ({Mode}) processed {Processed}/{Selected} events for integration {IntegrationId}: {Outcomes}", job.JobId, payload.Mode, processed, selection.Count, payload.IntegrationId, string.Join(", ", outcomes.Select(o => $"{o.Key}={o.Value}")));
    }
}
