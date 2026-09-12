using DeNoise.Application.Mapping;
using DeNoise.Domain.Alerts;
using DeNoise.Domain.Audit;
using DeNoise.Domain.Episodes;

namespace DeNoise.Application.Processing;

/// <summary>Reads the raw event by its primary key (received_at, event_id) — a single-partition lookup.</summary>
public interface IRawEventReader
{
    Task<RawEvent?> GetAsync(Guid eventId, DateTimeOffset receivedAt, CancellationToken ct = default);
    /// <summary>Lookup without the partition key (preview by id); scans the integration's partitions.</summary>
    Task<RawEvent?> FindAsync(Guid integrationId, Guid eventId, CancellationToken ct = default);
    /// <summary>Raw events of an integration received in [from, to), oldest first — the historical replay selection.</summary>
    Task<IReadOnlyList<(Guid EventId, DateTimeOffset ReceivedAt)>> ListAsync(Guid integrationId, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken ct = default);
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

    // --- Heartbeats (milestone 6) ---

    /// <summary>The heartbeat row, locked <c>FOR UPDATE</c> (pings and ticks serialise on it).</summary>
    Task<Domain.Heartbeats.Heartbeat?> FindHeartbeatForUpdateAsync(Guid heartbeatId, CancellationToken ct = default);
    /// <summary>Heartbeats whose deadline (or lateness) has passed, locked <c>FOR UPDATE SKIP LOCKED</c> so scheduler replicas share the work.</summary>
    Task<IReadOnlyList<Domain.Heartbeats.Heartbeat>> ClaimDueHeartbeatsAsync(DateTimeOffset now, int limit, CancellationToken ct = default);
    /// <summary>Heartbeats that may be paused or resumed by maintenance (auto-pause enabled, not paused by a person).</summary>
    Task<IReadOnlyList<Domain.Heartbeats.Heartbeat>> ListMaintenanceCandidatesForUpdateAsync(CancellationToken ct = default);
    Task<long> NextHeartbeatRunSeqAsync(Guid heartbeatId, CancellationToken ct = default);
    void AddHeartbeatRun(Domain.Heartbeats.HeartbeatRun run);
    /// <summary>The open group for (rule, key), locked <c>FOR UPDATE</c> (04 §7.4: one open group per rule and key).</summary>
    Task<Domain.Episodes.AlertGroup?> FindOpenGroupForUpdateAsync(Guid ruleId, string keyValues, CancellationToken ct = default);
    Task<Domain.Episodes.AlertGroup?> FindGroupForUpdateAsync(Guid groupId, CancellationToken ct = default);
    void AddGroup(Domain.Episodes.AlertGroup group);
    /// <summary>Every open episode, locked <c>FOR UPDATE</c> — suppression start/end re-evaluates all of them (spec §16.3).</summary>
    Task<IReadOnlyList<Episode>> ListOpenEpisodesForUpdateAsync(CancellationToken ct = default);
    /// <summary>Pending outbox rows of an episode become <c>suppressed</c> (a suppression began after they were staged).</summary>
    Task<int> SuppressPendingOutboxAsync(Guid episodeId, CancellationToken ct = default);
    /// <summary>Suppressed outbox rows of an episode become <c>coalesced</c>: muted notifications are never replayed (spec §16.3).</summary>
    Task<int> CoalesceSuppressedOutboxAsync(Guid episodeId, string reason, CancellationToken ct = default);
    /// <summary>Keeps the last <paramref name="keep"/> runs of a heartbeat (the ring of spec §13.3.3).</summary>
    Task<int> TrimHeartbeatRunsAsync(Guid heartbeatId, int keep, CancellationToken ct = default);
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

    /// <summary>Reachability probe of the source API (<c>api_probe</c> coverage method, spec §13.5): supporting evidence only.</summary>
    Task<ProbeResult> ProbeAsync(Domain.Integrations.Integration integration, CancellationToken ct = default)
        => Task.FromResult(new ProbeResult(false, false, "no api probe for this integration type"));
}

/// <summary>Outcome of an <c>api_probe</c>: <see cref="Supported"/> false when the type has no adapter or no credentials.</summary>
public sealed record ProbeResult(bool Supported, bool Ok, string? Detail);

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

    /// <summary>
    /// An episode the Hub opened itself with ownership already decided (heartbeat miss: the definition names the team). Routing rules are
    /// skipped; the ack deadline, lifecycle exemption and the given notification type to the team's destinations are staged as for any open.
    /// </summary>
    Task OnPreassignedOpenAsync(Episode episode, NormalisedEvent evt, Domain.Integrations.Integration integration, Domain.Teams.Team team, string notificationType, object? detail, IProcessingSession session, CancellationToken ct) => Task.CompletedTask;
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
