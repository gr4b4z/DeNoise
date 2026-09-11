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

    // --- Lifecycle and coverage (milestone 5) ---

    /// <summary>Open episodes of an integration, locked <c>FOR UPDATE</c> (coverage transitions touch all of them, 04 §2.1).</summary>
    Task<IReadOnlyList<Episode>> ListOpenEpisodesForIntegrationForUpdateAsync(Guid integrationId, CancellationToken ct = default);
    /// <summary>Pending timers of the given kinds for an integration become <c>suspended</c> (coverage lost). Returns the number affected.</summary>
    Task<int> SuspendJobsAsync(Guid integrationId, IReadOnlyCollection<string> kinds, CancellationToken ct = default);
    /// <summary>Suspended timers of the given kinds become <c>pending</c>, due no earlier than <paramref name="notBefore"/> (coverage restored). Returns the number affected.</summary>
    Task<int> ResumeJobsAsync(Guid integrationId, IReadOnlyCollection<string> kinds, DateTimeOffset notBefore, CancellationToken ct = default);
    /// <summary>Marks one job suspended (auto-resolve guard 3, 04 §5.3).</summary>
    Task<bool> SuspendJobAsync(Guid jobId, CancellationToken ct = default);
    /// <summary>Moves a claimed job back to <c>pending</c> at a new time with fresh guards — the same row, since only one live timer per episode and kind may exist.</summary>
    Task<bool> RescheduleJobAsync(Guid jobId, DateTimeOffset notBefore, int expectedVersion, DateTimeOffset expectedLastSeen, string payload, CancellationToken ct = default);
    /// <summary>The integration's coverage row, locked <c>FOR UPDATE</c>; null when no signal has ever been recorded.</summary>
    Task<Domain.Ops.CoverageState?> GetCoverageForUpdateAsync(Guid integrationId, CancellationToken ct = default);
    void AddCoverage(Domain.Ops.CoverageState state);
    /// <summary>Mapping failures recorded for the integration since <paramref name="since"/> (guard 5: mapping health).</summary>
    Task<int> CountMappingFailuresAsync(Guid integrationId, DateTimeOffset since, CancellationToken ct = default);
    /// <summary>Oldest unprocessed <c>normalise</c> job of the integration (guard 4: processing backlog); null when the queue is empty.</summary>
    Task<DateTimeOffset?> OldestPendingNormaliseAsync(Guid integrationId, CancellationToken ct = default);
    /// <summary>Open episodes carrying the given lifecycle policy (impact preview and rescheduling on activation).</summary>
    Task<IReadOnlyList<Episode>> ListOpenEpisodesByLifecyclePolicyForUpdateAsync(Guid policyId, CancellationToken ct = default);
}

/// <summary>What a source-state query returned (04 §5.3 guard 7).</summary>
public enum StateQueryOutcome
{
    /// <summary>The integration has no <c>state_query</c> capability or no adapter is registered.</summary>
    Unsupported,
    Active,
    NotActive,
    Error,
}

public sealed record StateQueryResult(StateQueryOutcome Outcome, string? Detail = null);

/// <summary>Asks the source whether the condition is still active (<c>queryable_state</c>, spec §12.1). Adapters arrive with milestone 8.</summary>
public interface IStateQueryAdapter
{
    Task<StateQueryResult> QueryAsync(Domain.Integrations.Integration integration, Episode episode, CancellationToken ct = default);
}

public sealed class NoStateQueryAdapter : IStateQueryAdapter
{
    public Task<StateQueryResult> QueryAsync(Domain.Integrations.Integration integration, Episode episode, CancellationToken ct = default)
        => Task.FromResult(new StateQueryResult(StateQueryOutcome.Unsupported, "no state query adapter for this integration type"));
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

    /// <summary>A closure without a source event (auto-resolve, expiry, coverage): cancel timers and notify prior recipients (04 §6 "resolved / cancelled / expired").</summary>
    Task OnSystemClosureAsync(Episode episode, IProcessingSession session, CancellationToken ct) => Task.CompletedTask;
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
