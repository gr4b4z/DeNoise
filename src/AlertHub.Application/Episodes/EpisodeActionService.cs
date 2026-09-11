using System.Text.Json;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Auth;
using AlertHub.Application.Integrations;
using AlertHub.Application.Notifications;
using AlertHub.Application.Processing;
using AlertHub.Application.Realtime;
using AlertHub.Application.Teams;
using AlertHub.Domain.Audit;
using AlertHub.Domain.Common;
using AlertHub.Domain.Episodes;
using AlertHub.Domain.Notifications;
using AlertHub.Domain.Ops;
using AlertHub.Domain.Users;
using Microsoft.Extensions.Options;

namespace AlertHub.Application.Episodes;

/// <summary>Caller-supplied <c>If-Match</c> version did not match; the API answers 409 with the current representation (06 §1).</summary>
public sealed class VersionConflictException(Guid episodeId, int currentVersion) : Exception($"episode {episodeId} is at version {currentVersion}")
{
    public Guid EpisodeId { get; } = episodeId;
    public int CurrentVersion { get; } = currentVersion;
}

public sealed record ActionResult(Episode Episode, bool Changed);

/// <summary>
/// Operator actions (04 §2.2, 06 §4). Each action runs in one transaction: re-read the episode <c>FOR UPDATE</c>,
/// check the expected version, apply, timeline + audit + outbox + timers, commit, then publish the change.
/// Permission and scope are checked per item; out-of-scope targets surface as 404.
/// </summary>
public sealed class EpisodeActionService(
    IProcessingUnitOfWork uow, IIntegrationRepository integrations, ITeamRepository teams, IDestinationRepository destinations, IUserRepository users,
    IChangeBroadcaster broadcaster, IOptions<NotificationOptions> options, TimeProvider time)
{
    public Task<ActionResult> AcknowledgeAsync(AlertHubPrincipal actor, Guid episodeId, int expectedVersion, bool force, string correlationId, CancellationToken ct = default)
        => RunAsync(actor, Permissions.EpisodeAck, episodeId, expectedVersion, correlationId, async (episode, session, now) =>
        {
            if (episode.HandlingState == HandlingState.Acknowledged)
            {
                if (episode.AssigneeId == actor.UserId) return false;
                if (!force) throw new InvalidEpisodeTransitionException("already acknowledged by someone else; take over explicitly with force=true");
                var previous = episode.AssigneeId;
                episode.Assign(null, actor.UserId, now);
                session.AddEpisodeEvent(Timeline(episode, now, EpisodeEventKind.Takeover, actor.UserId, new { previousAssignee = previous }));
                session.AddAudit(Audit(actor, "episode.takeover", episode, correlationId, new { previousAssignee = previous }));
                return true;
            }
            episode.Acknowledge(actor.UserId, now);
            await session.CancelJobsAsync(episode.EpisodeId, [JobKinds.AckDeadline, JobKinds.EscalationStep], ct);
            session.AddEpisodeEvent(Timeline(episode, now, EpisodeEventKind.Ack, actor.UserId, new { assignee = episode.AssigneeId }));
            session.AddAudit(Audit(actor, "episode.ack", episode, correlationId, new { assignee = episode.AssigneeId }));
            await StageAsync(session, episode, NotificationTypes.EpisodeAcknowledged, now, ct);
            return true;
        }, ct);

    public Task<ActionResult> AssignAsync(AlertHubPrincipal actor, Guid episodeId, int expectedVersion, Guid? teamId, Guid? userId, string correlationId, CancellationToken ct = default)
        => RunAsync(actor, Permissions.EpisodeAssign, episodeId, expectedVersion, correlationId, async (episode, session, now) =>
        {
            if (teamId is { } t && await teams.GetAsync(t, ct) is null) throw new KeyNotFoundException($"team {t} not found");
            if (userId is { } u && await users.GetAsync(u, ct) is null) throw new KeyNotFoundException($"user {u} not found");
            var before = new { teamId = episode.OwningTeamId, userId = episode.AssigneeId };
            episode.Assign(teamId, userId, now);
            if (teamId is not null) episode.RoutingCorrectionRequired = false;
            session.AddEpisodeEvent(Timeline(episode, now, EpisodeEventKind.Assign, actor.UserId, new { teamId = episode.OwningTeamId, userId = episode.AssigneeId, manual = true }));
            session.AddAudit(Audit(actor, "episode.assign", episode, correlationId, new { teamId = episode.OwningTeamId, userId = episode.AssigneeId }, before));
            return true;
        }, ct);

    public Task<ActionResult> NoteAsync(AlertHubPrincipal actor, Guid episodeId, int expectedVersion, string text, string correlationId, CancellationToken ct = default)
        => RunAsync(actor, Permissions.EpisodeNote, episodeId, expectedVersion, correlationId, (episode, session, now) =>
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("note text is required");
            episode.Touch(now); // notes are allowed on closed episodes (04 §2.2)
            session.AddEpisodeEvent(Timeline(episode, now, EpisodeEventKind.Note, actor.UserId, new { text = text.Trim() }));
            session.AddAudit(Audit(actor, "episode.note", episode, correlationId, new { length = text.Trim().Length }));
            return Task.FromResult(true);
        }, ct, allowClosed: true);

    public Task<ActionResult> CloseAsync(AlertHubPrincipal actor, Guid episodeId, int expectedVersion, string reason, string correlationId, CancellationToken ct = default)
        => RunAsync(actor, Permissions.EpisodeClose, episodeId, expectedVersion, correlationId, async (episode, session, now) =>
        {
            episode.CloseManually(reason, now);
            await session.CancelJobsAsync(episode.EpisodeId, [JobKinds.AckDeadline, JobKinds.FollowUp, JobKinds.EscalationStep, JobKinds.AutoResolve, JobKinds.VerifyState, JobKinds.StaleReview, JobKinds.AdminExpiry], ct);
            session.AddEpisodeEvent(Timeline(episode, now, EpisodeEventKind.Close, actor.UserId, new { reason = reason.Trim(), condition = episode.ConditionState }));
            session.AddAudit(Audit(actor, "episode.close", episode, correlationId, new { reason = reason.Trim(), condition = episode.ConditionState, evidence = Evidence.Human }));
            await StageClosedAsync(session, episode, now, ct);
            return true;
        }, ct);

    public Task<ActionResult> RestoreAsync(AlertHubPrincipal actor, Guid episodeId, int expectedVersion, string? reason, string correlationId, CancellationToken ct = default)
        => RunAsync(actor, Permissions.EpisodeRestore, episodeId, expectedVersion, correlationId, async (episode, session, now) =>
        {
            var from = episode.ClosureReason;
            var successor = await session.FindOpenEpisodeForUpdateAsync(episode.Fingerprint, ct);
            if (successor is not null && successor.EpisodeId != episode.EpisodeId)
            {
                throw new InvalidEpisodeTransitionException($"a newer episode for this identity is open ({successor.EpisodeId}); work there instead of restoring");
            }
            episode.Restore(now);
            if (episode.AssigneeId is null) episode.Assign(null, actor.UserId, now);
            session.AddEpisodeEvent(Timeline(episode, now, EpisodeEventKind.Restore, actor.UserId, new { from, reason }));
            session.AddAudit(Audit(actor, "episode.restore", episode, correlationId, new { from, reason }));
            return true;
        }, ct, allowClosed: true);

    public Task<ActionResult> SilenceAsync(AlertHubPrincipal actor, Guid episodeId, int expectedVersion, DateTimeOffset until, string reason, string correlationId, CancellationToken ct = default)
        => RunAsync(actor, Permissions.EpisodeSilence, episodeId, expectedVersion, correlationId, (episode, session, now) =>
        {
            if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("a reason is required");
            if (until <= now) throw new ArgumentException("until must be in the future");
            if (until - now > TimeSpan.FromHours(24) && !actor.Has(Permissions.SuppressionCreateLong)) throw new ForbiddenException(Permissions.SuppressionCreateLong, "silences longer than 24 h need integration_admin");
            episode.SuppressedUntil = until;
            episode.SuppressionSource = "silence";
            episode.Touch(now);
            session.AddEpisodeEvent(Timeline(episode, now, EpisodeEventKind.Suppress, actor.UserId, new { until, reason = reason.Trim() }));
            session.AddAudit(Audit(actor, "episode.silence", episode, correlationId, new { until, reason = reason.Trim() }));
            return Task.FromResult(true);
        }, ct);

    /// <summary>Schedules a <c>verify_state</c> job (handler arrives with milestone 5); 202 semantics.</summary>
    public Task<ActionResult> RefreshStateAsync(AlertHubPrincipal actor, Guid episodeId, string correlationId, CancellationToken ct = default)
        => RunAsync(actor, Permissions.EpisodeRead, episodeId, null, correlationId, (episode, session, now) =>
        {
            session.AddJob(new Job
            {
                JobId = Ids.New(time),
                Kind = JobKinds.VerifyState,
                NotBefore = now,
                EpisodeId = episode.EpisodeId,
                IntegrationId = episode.IntegrationId,
                ExpectedVersion = episode.Version,
                Payload = JsonSerializer.Serialize(new { episodeId = episode.EpisodeId, requestedBy = actor.UserId }, JsonDefaults.Stored),
                CreatedAt = now,
                UpdatedAt = now,
            });
            session.AddAudit(Audit(actor, "episode.refresh_state", episode, correlationId, null));
            return Task.FromResult(false);
        }, ct);

    private async Task<ActionResult> RunAsync(AlertHubPrincipal actor, string permission, Guid episodeId, int? expectedVersion, string correlationId,
        Func<Episode, IProcessingSession, DateTimeOffset, Task<bool>> apply, CancellationToken ct, bool allowClosed = false)
    {
        if (!actor.Has(permission)) throw new ForbiddenException(permission);
        Episode episode;
        bool changed;
        try
        {
            (episode, changed) = await uow.RunAsync(async session =>
            {
                var episode = await session.FindEpisodeAsync(episodeId, ct) ?? throw new OutOfScopeException("episode", episodeId);
                if (!actor.CanSeeScope(episode.AccessScope)) throw new OutOfScopeException("episode", episodeId);
                if (expectedVersion is { } expected && episode.Version != expected) throw new VersionConflictException(episodeId, episode.Version);
                if (!allowClosed && !episode.IsOpen) throw new InvalidEpisodeTransitionException("episode is closed");
                var now = time.GetUtcNow();
                var changed = await apply(episode, session, now);
                return (episode, changed);
            }, ct);
        }
        catch (ProcessingConflictException)
        {
            // Two users raced past the If-Match check (scenario "Two users take ownership"): the loser gets 409 with the winner's version.
            var current = await uow.RunAsync(session => session.FindEpisodeAsync(episodeId, ct), ct);
            throw new VersionConflictException(episodeId, current?.Version ?? 0);
        }
        if (changed)
        {
            try
            {
                await broadcaster.PublishAsync(ChangeEvent.ForEpisode(episode), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _ = ex; // best effort; clients resync
            }
        }
        return new ActionResult(episode, changed);
    }

    private async Task StageAsync(IProcessingSession session, Episode episode, string type, DateTimeOffset now, CancellationToken ct)
    {
        var integration = await integrations.GetCurrentAsync(episode.IntegrationId, ct);
        if (integration is null || integration.Shadow || episode.OwningTeamId is null) return;
        var team = await teams.GetAsync(episode.OwningTeamId.Value, ct);
        var model = NotificationModel.ForEpisode(type, episode, null, integration, team, options.Value.PublicBaseUrl).ToJsonString(JsonDefaults.Stored);
        foreach (var d in (await destinations.ListForTeamAsync(episode.OwningTeamId.Value, ct)).Where(d => d.Active && d.Subscribes(type)))
        {
            session.AddOutbox(new OutboxMessage { OutboxId = Ids.New(time), EpisodeId = episode.EpisodeId, Type = type, DestinationId = d.DestinationId, Payload = model, NotBefore = now, CreatedAt = now });
        }
    }

    private async Task StageClosedAsync(IProcessingSession session, Episode episode, DateTimeOffset now, CancellationToken ct)
    {
        var integration = await integrations.GetCurrentAsync(episode.IntegrationId, ct);
        if (integration is null || integration.Shadow) return;
        var recipients = await session.PriorRecipientsAsync(episode.EpisodeId, ct);
        if (recipients.Count == 0) return;
        var team = episode.OwningTeamId is { } t ? await teams.GetAsync(t, ct) : null;
        var model = NotificationModel.ForEpisode(NotificationTypes.EpisodeClosed, episode, null, integration, team, options.Value.PublicBaseUrl).ToJsonString(JsonDefaults.Stored);
        foreach (var id in recipients)
        {
            var d = await destinations.GetAsync(id, ct);
            if (d is null || !d.Subscribes(NotificationTypes.EpisodeClosed)) continue;
            session.AddOutbox(new OutboxMessage { OutboxId = Ids.New(time), EpisodeId = episode.EpisodeId, Type = NotificationTypes.EpisodeClosed, DestinationId = d.DestinationId, Payload = model, NotBefore = now, CreatedAt = now });
        }
    }

    private EpisodeEvent Timeline(Episode episode, DateTimeOffset now, string kind, Guid actorId, object detail) => new()
    {
        Id = Ids.New(time),
        EpisodeId = episode.EpisodeId,
        At = now,
        Kind = kind,
        ActorId = actorId,
        Detail = JsonSerializer.Serialize(detail, JsonDefaults.Stored),
    };

    private AuditEntry Audit(AlertHubPrincipal actor, string action, Episode episode, string correlationId, object? after, object? before = null) => new()
    {
        Id = Ids.New(time),
        At = time.GetUtcNow(),
        ActorType = ActorTypes.User,
        ActorId = actor.UserId.ToString(),
        ActorDisplay = actor.Username,
        Action = action,
        TargetType = "episode",
        TargetId = episode.EpisodeId.ToString(),
        AccessScope = episode.AccessScope,
        Before = before is null ? null : JsonSerializer.Serialize(before, JsonDefaults.Stored),
        After = after is null ? null : JsonSerializer.Serialize(after, JsonDefaults.Stored),
        CorrelationId = correlationId,
    };
}
