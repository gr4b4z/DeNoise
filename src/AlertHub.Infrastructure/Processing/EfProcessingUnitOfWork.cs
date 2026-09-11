using AlertHub.Application.Processing;
using AlertHub.Domain.Alerts;
using AlertHub.Domain.Audit;
using AlertHub.Domain.Episodes;
using AlertHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AlertHub.Infrastructure.Processing;

/// <summary>
/// One processing transaction on a fresh <see cref="AlertHubDbContext"/>: begin → work → SaveChanges → commit.
/// Unique-index violations on the open-episode index and EF version conflicts become <see cref="ProcessingConflictException"/>;
/// the ledger's own conflict is handled inline by <see cref="EfProcessingSession.TryRecordAppliedAsync"/> and never throws.
/// </summary>
public sealed class EfProcessingUnitOfWork(IServiceScopeFactory scopes) : IProcessingUnitOfWork
{
    public async Task<T> RunAsync<T>(Func<IProcessingSession, Task<T>> work, CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AlertHubDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var session = new EfProcessingSession(db);
            var result = await work(session);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return result;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ProcessingConflictException("episode version changed concurrently", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            throw new ProcessingConflictException($"unique constraint {pg.ConstraintName} violated concurrently", ex);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected)
        {
            throw new ProcessingConflictException($"database conflict {ex.SqlState}", ex);
        }
    }
}

internal sealed class EfProcessingSession(AlertHubDbContext db) : IProcessingSession
{
    public async Task<bool> TryRecordAppliedAsync(AppliedEvent applied, CancellationToken ct = default)
    {
        var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO alert.applied_event (integration_id, delivery_key, event_id, applied_at, outcome)
            VALUES ({applied.IntegrationId}, {applied.DeliveryKey}, {applied.EventId}, {applied.AppliedAt}, {applied.Outcome})
            ON CONFLICT (integration_id, delivery_key) DO NOTHING
            """, ct);
        return rows == 1;
    }

    public Task RecordDuplicateAsync(Guid integrationId, string deliveryKey, DateTimeOffset now, CancellationToken ct = default)
        => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO alert.delivery_duplicate (integration_id, delivery_key, count, first_seen, last_seen)
            VALUES ({integrationId}, {deliveryKey}, 1, {now}, {now})
            ON CONFLICT (integration_id, delivery_key) DO UPDATE SET count = alert.delivery_duplicate.count + 1, last_seen = EXCLUDED.last_seen
            """, ct);

    public async Task<Guid?> FindEpisodeIdForDeliveryKeyAsync(Guid integrationId, string deliveryKey, CancellationToken ct = default)
    {
        var eventId = await db.Set<AppliedEvent>().AsNoTracking()
            .Where(a => a.IntegrationId == integrationId && a.DeliveryKey == deliveryKey)
            .Select(a => (Guid?)a.EventId).SingleOrDefaultAsync(ct);
        if (eventId is null) return null;
        return await db.NormalisedEvents.AsNoTracking().Where(n => n.EventId == eventId).Select(n => n.EpisodeId).SingleOrDefaultAsync(ct);
    }

    public Task EnsureIdentityAsync(AlertIdentity identity, CancellationToken ct = default)
        => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO alert.identity (fingerprint, integration_id, access_scope, identity_version, components, first_seen, episode_count)
            VALUES ({Fingerprint.ToBytes(identity.Fingerprint)}, {identity.IntegrationId}, {identity.AccessScope}, {identity.IdentityVersion}, {identity.Components}::jsonb, {identity.FirstSeen}, 0)
            ON CONFLICT (fingerprint) DO NOTHING
            """, ct);

    public Task IncrementIdentityEpisodeCountAsync(string fingerprint, CancellationToken ct = default)
        => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE alert.identity SET episode_count = episode_count + 1 WHERE fingerprint = {Fingerprint.ToBytes(fingerprint)}", ct);

    public Task<Episode?> FindOpenEpisodeForUpdateAsync(string fingerprint, CancellationToken ct = default)
        => db.Episodes.FromSqlInterpolated($"SELECT * FROM alert.episode WHERE fingerprint = {Fingerprint.ToBytes(fingerprint)} AND handling_state <> 'closed' FOR UPDATE")
            .SingleOrDefaultAsync(ct);

    public Task<Episode?> FindLatestClosedEpisodeAsync(string fingerprint, CancellationToken ct = default)
        => db.Episodes.Where(e => e.Fingerprint == fingerprint && e.HandlingState == HandlingState.Closed)
            .OrderByDescending(e => e.ClosedAt).FirstOrDefaultAsync(ct);

    public Task<Episode?> FindEpisodeAsync(Guid episodeId, CancellationToken ct = default)
        => db.Episodes.SingleOrDefaultAsync(e => e.EpisodeId == episodeId, ct);

    public Task<SourceInstanceState?> GetSourceInstanceStateAsync(Guid integrationId, string sourceAlertId, CancellationToken ct = default)
        => db.Set<SourceInstanceState>().AsNoTracking().SingleOrDefaultAsync(s => s.IntegrationId == integrationId && s.SourceAlertId == sourceAlertId, ct);

    public Task UpsertSourceInstanceStateAsync(Guid integrationId, string sourceAlertId, DateTimeOffset resolvedAt, CancellationToken ct = default)
        => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO alert.source_instance_state (integration_id, source_alert_id, resolved_at)
            VALUES ({integrationId}, {sourceAlertId}, {resolvedAt})
            ON CONFLICT (integration_id, source_alert_id) DO UPDATE SET resolved_at = GREATEST(alert.source_instance_state.resolved_at, EXCLUDED.resolved_at)
            """, ct);

    public void AddEpisode(Episode episode) => db.Episodes.Add(episode);
    public void AddNormalisedEvent(NormalisedEvent evt) => db.NormalisedEvents.Add(evt);
    public void AddEpisodeEvent(EpisodeEvent evt) => db.EpisodeEvents.Add(evt);
    public void AddAudit(AuditEntry entry) => db.AuditEntries.Add(entry);
    public void AddMappingFailure(MappingFailure failure) => db.MappingFailures.Add(failure);
    public void AddOutbox(Domain.Ops.OutboxMessage message) => db.Outbox.Add(message);
    public void AddJob(Domain.Ops.Job job) => db.Jobs.Add(job);

    public Task<int> CancelJobsAsync(Guid episodeId, IReadOnlyCollection<string> kinds, CancellationToken ct = default)
    {
        var kindArray = kinds.ToArray();
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ops.job SET status = 'cancelled', updated_at = now()
            WHERE episode_id = {episodeId} AND kind = ANY({kindArray}) AND status IN ('pending','suspended')
            """, ct);
    }

    public async Task<IReadOnlyList<Episode>> ListOpenEpisodesForIntegrationForUpdateAsync(Guid integrationId, CancellationToken ct = default)
        => await db.Episodes.FromSqlInterpolated($"SELECT * FROM alert.episode WHERE integration_id = {integrationId} AND handling_state <> 'closed' ORDER BY first_seen FOR UPDATE").ToListAsync(ct);

    public Task<int> SuspendJobsAsync(Guid integrationId, IReadOnlyCollection<string> kinds, CancellationToken ct = default)
    {
        var kindArray = kinds.ToArray();
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ops.job SET status = 'suspended', updated_at = now()
            WHERE integration_id = {integrationId} AND kind = ANY({kindArray}) AND status = 'pending'
            """, ct);
    }

    public Task<int> ResumeJobsAsync(Guid integrationId, IReadOnlyCollection<string> kinds, DateTimeOffset notBefore, CancellationToken ct = default)
    {
        var kindArray = kinds.ToArray();
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ops.job SET status = 'pending', not_before = GREATEST(not_before, {notBefore}), updated_at = now()
            WHERE integration_id = {integrationId} AND kind = ANY({kindArray}) AND status = 'suspended'
            """, ct);
    }

    public async Task<bool> SuspendJobAsync(Guid jobId, CancellationToken ct = default)
        => await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ops.job SET status = 'suspended', reserved_by = NULL, reserved_until = NULL, updated_at = now() WHERE job_id = {jobId} AND status IN ('pending','reserved')", ct) == 1;

    public async Task<bool> RescheduleJobAsync(Guid jobId, DateTimeOffset notBefore, int expectedVersion, DateTimeOffset expectedLastSeen, string payload, CancellationToken ct = default)
        => await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ops.job SET status = 'pending', not_before = {notBefore}, expected_version = {expectedVersion}, expected_last_seen = {expectedLastSeen},
                payload = {payload}::jsonb, reserved_by = NULL, reserved_until = NULL, attempts = 0, last_error = NULL, updated_at = now()
            WHERE job_id = {jobId} AND status IN ('pending','reserved','suspended')
            """, ct) == 1;

    public Task<Domain.Ops.CoverageState?> GetCoverageForUpdateAsync(Guid integrationId, CancellationToken ct = default)
        => db.CoverageStates.FromSqlInterpolated($"SELECT * FROM ops.coverage_state WHERE integration_id = {integrationId} FOR UPDATE").SingleOrDefaultAsync(ct);

    public void AddCoverage(Domain.Ops.CoverageState state) => db.CoverageStates.Add(state);

    public Task<int> CountMappingFailuresAsync(Guid integrationId, DateTimeOffset since, CancellationToken ct = default)
        => db.MappingFailures.CountAsync(f => f.IntegrationId == integrationId && f.RawReceivedAt >= since, ct);

    public async Task<DateTimeOffset?> OldestPendingNormaliseAsync(Guid integrationId, CancellationToken ct = default)
    {
        var oldest = await db.Jobs.AsNoTracking()
            .Where(j => j.IntegrationId == integrationId && j.Kind == Domain.Ops.JobKinds.Normalise && (j.Status == Domain.Ops.JobStatus.Pending || j.Status == Domain.Ops.JobStatus.Reserved))
            .OrderBy(j => j.CreatedAt).Select(j => (DateTimeOffset?)j.CreatedAt).FirstOrDefaultAsync(ct);
        return oldest;
    }

    public async Task<IReadOnlyList<Episode>> ListOpenEpisodesByLifecyclePolicyForUpdateAsync(Guid policyId, CancellationToken ct = default)
        => await db.Episodes.FromSqlInterpolated($"SELECT * FROM alert.episode WHERE lifecycle_policy_id = {policyId} AND handling_state <> 'closed' ORDER BY first_seen FOR UPDATE").ToListAsync(ct);

    public Task<Domain.Heartbeats.Heartbeat?> FindHeartbeatForUpdateAsync(Guid heartbeatId, CancellationToken ct = default)
        => db.Heartbeats.FromSqlInterpolated($"SELECT * FROM hb.heartbeat WHERE heartbeat_id = {heartbeatId} FOR UPDATE").SingleOrDefaultAsync(ct);

    public async Task<IReadOnlyList<Domain.Heartbeats.Heartbeat>> ClaimDueHeartbeatsAsync(DateTimeOffset now, int limit, CancellationToken ct = default)
        => await db.Heartbeats.FromSqlInterpolated($"SELECT * FROM hb.heartbeat WHERE state IN ('healthy','late') AND expected_next < {now} ORDER BY expected_next LIMIT {limit} FOR UPDATE SKIP LOCKED").ToListAsync(ct);

    public async Task<IReadOnlyList<Domain.Heartbeats.Heartbeat>> ListMaintenanceCandidatesForUpdateAsync(CancellationToken ct = default)
        => await db.Heartbeats.FromSqlInterpolated($"SELECT * FROM hb.heartbeat WHERE auto_pause_during_maintenance AND (state <> 'paused' OR paused_by_maintenance) ORDER BY heartbeat_id FOR UPDATE SKIP LOCKED").ToListAsync(ct);

    public async Task<long> NextHeartbeatRunSeqAsync(Guid heartbeatId, CancellationToken ct = default)
        => (await db.HeartbeatRuns.Where(r => r.HeartbeatId == heartbeatId).MaxAsync(r => (long?)r.Seq, ct) ?? 0) + 1;

    public void AddHeartbeatRun(Domain.Heartbeats.HeartbeatRun run) => db.HeartbeatRuns.Add(run);

    public Task<int> TrimHeartbeatRunsAsync(Guid heartbeatId, int keep, CancellationToken ct = default)
        => db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM hb.run WHERE heartbeat_id = {heartbeatId} AND seq <= (SELECT max(seq) FROM hb.run WHERE heartbeat_id = {heartbeatId}) - {keep}
            """, ct);

    public async Task<IReadOnlyList<Guid>> PriorRecipientsAsync(Guid episodeId, CancellationToken ct = default)
        => await db.Outbox.AsNoTracking()
            .Where(o => o.EpisodeId == episodeId && o.Status != Domain.Ops.OutboxStatus.Cancelled && o.Status != Domain.Ops.OutboxStatus.Coalesced)
            .Select(o => o.DestinationId).Distinct().ToListAsync(ct);
}
