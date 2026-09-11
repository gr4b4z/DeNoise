using AlertHub.Application.Mapping;
using AlertHub.Domain.Alerts;
using AlertHub.Domain.Audit;
using AlertHub.Domain.Episodes;

namespace AlertHub.Application.Processing;

/// <summary>Reads the raw event by its primary key (received_at, event_id) — a single-partition lookup.</summary>
public interface IRawEventReader
{
    Task<RawEvent?> GetAsync(Guid eventId, DateTimeOffset receivedAt, CancellationToken ct = default);
}

/// <summary>Active mapping versions of an integration, in evaluation order (07 §1 <c>applies_when</c>: first match wins).</summary>
public interface IMappingResolver
{
    Task<IReadOnlyList<MappingDocument>> GetActiveAsync(Guid integrationId, CancellationToken ct = default);

    /// <summary>Drops any cached view of the integration's active mappings (called after activation; other replicas refresh on TTL).</summary>
    void Invalidate(Guid integrationId);
}

/// <summary>
/// Everything the processing transaction touches (03 §2 "Process"). One instance = one database transaction;
/// staged rows commit together or not at all. Implementations translate unique-index and version conflicts
/// into <see cref="ProcessingConflictException"/> so the processor can retry.
/// </summary>
public interface IProcessingSession
{
    /// <summary>Inserts into the idempotency ledger; false means the delivery key was already applied (duplicate).</summary>
    Task<bool> TryRecordAppliedAsync(AppliedEvent applied, CancellationToken ct = default);
    Task RecordDuplicateAsync(Guid integrationId, string deliveryKey, DateTimeOffset now, CancellationToken ct = default);
    Task<Guid?> FindEpisodeIdForDeliveryKeyAsync(Guid integrationId, string deliveryKey, CancellationToken ct = default);
    Task EnsureIdentityAsync(AlertIdentity identity, CancellationToken ct = default);
    Task IncrementIdentityEpisodeCountAsync(string fingerprint, CancellationToken ct = default);
    /// <summary>The open episode for the identity, locked <c>FOR UPDATE</c> for the rest of the transaction.</summary>
    Task<Episode?> FindOpenEpisodeForUpdateAsync(string fingerprint, CancellationToken ct = default);
    Task<Episode?> FindLatestClosedEpisodeAsync(string fingerprint, CancellationToken ct = default);
    Task<Episode?> FindEpisodeAsync(Guid episodeId, CancellationToken ct = default);
    Task<SourceInstanceState?> GetSourceInstanceStateAsync(Guid integrationId, string sourceAlertId, CancellationToken ct = default);
    Task UpsertSourceInstanceStateAsync(Guid integrationId, string sourceAlertId, DateTimeOffset resolvedAt, CancellationToken ct = default);
    void AddEpisode(Episode episode);
    void AddNormalisedEvent(NormalisedEvent evt);
    void AddEpisodeEvent(EpisodeEvent evt);
    void AddAudit(AuditEntry entry);
    void AddMappingFailure(MappingFailure failure);
    /// <summary>Stages an outbox row; committed with the transition (transactional outbox, spec §17.3).</summary>
    void AddOutbox(Domain.Ops.OutboxMessage message);
    /// <summary>Stages a timer job (<c>ack_deadline</c>, <c>escalation_step</c>, <c>auto_resolve</c>, …) in the same commit.</summary>
    void AddJob(Domain.Ops.Job job);
    /// <summary>Cancels pending/suspended timers of the given kinds for an episode (closure cancels timers, 04 §2).</summary>
    Task<int> CancelJobsAsync(Guid episodeId, IReadOnlyCollection<string> kinds, CancellationToken ct = default);
    /// <summary>Destinations that already received a notification about the episode (for <c>episode.closed</c> to prior recipients, 04 §6).</summary>
    Task<IReadOnlyList<Guid>> PriorRecipientsAsync(Guid episodeId, CancellationToken ct = default);
}

/// <summary>Runs one processing transaction. Conflicts surface as <see cref="ProcessingConflictException"/>.</summary>
public interface IProcessingUnitOfWork
{
    Task<T> RunAsync<T>(Func<IProcessingSession, Task<T>> work, CancellationToken ct = default);
}

/// <summary>A concurrent worker won the race for the same identity or episode version; the caller retries from scratch.</summary>
public sealed class ProcessingConflictException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Called inside the processing transaction after an episode transition so routing/notification (milestone 3)
/// can stage outbox rows and timers in the same commit (AGENTS.md rule 4).
/// </summary>
public interface ITransitionHook
{
    Task OnTransitionAsync(Episode episode, NormalisedEvent evt, EpisodeTransition transition, IProcessingSession session, CancellationToken ct);
}

/// <summary>Best-effort post-commit notification for the UI stream (ADR-12). Never inside the transaction.</summary>
public interface IEpisodeChangePublisher
{
    Task PublishAsync(Episode episode, CancellationToken ct);
}

public sealed class NoOpTransitionHook : ITransitionHook
{
    public Task OnTransitionAsync(Episode episode, NormalisedEvent evt, EpisodeTransition transition, IProcessingSession session, CancellationToken ct) => Task.CompletedTask;
}

public sealed class NoOpEpisodeChangePublisher : IEpisodeChangePublisher
{
    public Task PublishAsync(Episode episode, CancellationToken ct) => Task.CompletedTask;
}
