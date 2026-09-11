using AlertHub.Domain.Alerts;
using AlertHub.Domain.Common;

namespace AlertHub.Domain.Episodes;

/// <summary>
/// One continuous occurrence of a condition under one identity (spec §8; <c>alert.episode</c>, 05 §2).
/// Two independent dimensions: <see cref="ConditionState"/> and <see cref="HandlingState"/>. <see cref="Version"/>
/// increments on every persisted change and is the optimistic-concurrency token.
/// Transition tables: 04 §2.
/// </summary>
public sealed class Episode
{
    public Guid EpisodeId { get; init; }
    public required string Fingerprint { get; init; }
    public Guid IntegrationId { get; init; }
    public required string AccessScope { get; init; }
    public Guid? PreviousEpisodeId { get; init; }
    public string ConditionState { get; private set; } = Episodes.ConditionState.Firing;
    public string HandlingState { get; private set; } = Episodes.HandlingState.New;
    public Severity Severity { get; private set; }
    public bool IsActionable { get; private set; }
    public DateTimeOffset FirstSeen { get; private set; }
    public DateTimeOffset LastSeen { get; private set; }
    public DateTimeOffset LastReceivedAt { get; private set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public string? LastAppliedVersion { get; private set; }
    public int OccurrenceCount { get; private set; } = 1;
    public Guid? OwningTeamId { get; set; }
    public Guid? AssigneeId { get; set; }
    public Guid? RoutingRuleId { get; set; }
    public bool RoutingCorrectionRequired { get; set; }
    public DateTimeOffset? AckDeadlineAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; private set; }
    public Guid? AcknowledgedBy { get; private set; }
    public DateTimeOffset? FollowUpAt { get; set; }
    public DateTimeOffset? AutoResolveAt { get; set; }
    public Guid? LifecyclePolicyId { get; set; }
    public int? LifecyclePolicyVersion { get; set; }
    public string? LifecycleProfile { get; set; }
    /// <summary>Set by the <c>stale_review</c> timer (spec §12.5): the episode has waited past its review window without verification.</summary>
    public DateTimeOffset? StaleSince { get; set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public string? ClosureReason { get; private set; }
    public string? ResolutionEvidence { get; private set; }
    public string? ClosureNote { get; private set; }
    public string? RestoredFromReason { get; private set; }
    public DateTimeOffset? SuppressedUntil { get; set; }
    public string? SuppressionSource { get; set; }
    public Guid? GroupId { get; set; }
    public string? Summary { get; private set; }
    public string? ResourceName { get; private set; }
    public string? RuleName { get; private set; }
    public string? Service { get; private set; }
    public string? Environment { get; private set; }
    public string? SourceUrl { get; private set; }
    public string? RunbookUrl { get; private set; }
    public string? SourceAlertId { get; private set; }
    public int Version { get; private set; } = 1;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsOpen => HandlingState != Episodes.HandlingState.Closed;

    /// <summary>Opens a new episode from an effective <c>firing</c>, <c>update</c> (treated as firing) or <c>informational</c> event (04 §2.1 row 1 and last).</summary>
    public static Episode Open(NormalisedEvent evt, string accessScope, DateTimeOffset now, TimeProvider time, Guid? previousEpisodeId = null)
    {
        var informational = evt.EventType == EventTypes.Informational;
        var episode = new Episode
        {
            EpisodeId = Ids.New(time),
            Fingerprint = evt.Fingerprint ?? throw new ArgumentException("event has no fingerprint", nameof(evt)),
            IntegrationId = evt.IntegrationId,
            AccessScope = accessScope,
            PreviousEpisodeId = previousEpisodeId,
            ConditionState = informational ? Episodes.ConditionState.NotApplicable : Episodes.ConditionState.Firing,
            HandlingState = Episodes.HandlingState.New,
            Severity = evt.Severity,
            IsActionable = !informational && evt.Severity != Severity.Informational,
            FirstSeen = evt.EffectiveAt,
            LastSeen = evt.EffectiveAt,
            LastReceivedAt = evt.ReceivedAt,
            LastAppliedVersion = evt.SourceVersion,
            OccurrenceCount = 1,
            CreatedAt = now,
            UpdatedAt = now,
            LifecycleProfile = evt.LifecycleProfileHint,
        };
        episode.CopyDisplayFields(evt);
        return episode;
    }

    /// <summary>Effective <c>firing</c>/<c>update</c> on an open episode (04 §2.1 row 2). Returns the transition for notification decisions.</summary>
    public EpisodeTransition ApplySignal(NormalisedEvent evt, DateTimeOffset now)
    {
        EnsureOpen();
        var previousSeverity = Severity;
        var previousCondition = ConditionState;
        if (evt.EffectiveAt > LastSeen) LastSeen = evt.EffectiveAt;
        LastReceivedAt = evt.ReceivedAt;
        if (!string.IsNullOrEmpty(evt.SourceVersion)) LastAppliedVersion = evt.SourceVersion;
        OccurrenceCount++;
        if (evt.Severity.Rank() > Severity.Rank() || (Severity == Severity.Unknown && evt.Severity != Severity.Unknown))
        {
            Severity = evt.Severity;
        }
        if (ConditionState == Episodes.ConditionState.Unknown)
        {
            ConditionState = Episodes.ConditionState.Firing; // 04 §2.1: unknown + effective firing → firing
        }
        CopyDisplayFields(evt);
        Touch(now);
        return new EpisodeTransition(EpisodeTransitionKind.Updated, previousCondition, ConditionState, previousSeverity, Severity);
    }

    /// <summary>Effective <c>resolved</c> from the producer (04 §2.1 row 3): confirmed recovery, evidence <c>source</c>.</summary>
    public EpisodeTransition ApplySourceResolved(NormalisedEvent evt, DateTimeOffset now, bool keepOpenOnResolve = false)
    {
        EnsureOpen();
        var previousCondition = ConditionState;
        if (evt.EffectiveAt > LastSeen) LastSeen = evt.EffectiveAt;
        LastReceivedAt = evt.ReceivedAt;
        if (!string.IsNullOrEmpty(evt.SourceVersion)) LastAppliedVersion = evt.SourceVersion;
        ConditionState = Episodes.ConditionState.Resolved;
        if (!keepOpenOnResolve)
        {
            Close(Episodes.ClosureReason.SourceResolved, Evidence.Source, null, now);
        }
        Touch(now);
        return new EpisodeTransition(EpisodeTransitionKind.Resolved, previousCondition, ConditionState, Severity, Severity);
    }

    /// <summary>Producer invalidated the alert (04 §2.1): condition <c>not_applicable</c>, reason <c>source_cancelled</c>.</summary>
    public EpisodeTransition ApplySourceCancelled(NormalisedEvent evt, DateTimeOffset now)
    {
        EnsureOpen();
        var previousCondition = ConditionState;
        LastReceivedAt = evt.ReceivedAt;
        if (!string.IsNullOrEmpty(evt.SourceVersion)) LastAppliedVersion = evt.SourceVersion;
        ConditionState = Episodes.ConditionState.NotApplicable;
        Close(Episodes.ClosureReason.SourceCancelled, Evidence.Source, null, now);
        Touch(now);
        return new EpisodeTransition(EpisodeTransitionKind.Cancelled, previousCondition, ConditionState, Severity, Severity);
    }

    /// <summary>Raises severity without a source event (coverage episode <c>degraded</c> → <c>unavailable</c>, 04 §9). Never lowers it.</summary>
    public bool RaiseSeverity(Severity severity, DateTimeOffset now)
    {
        if (!IsOpen || severity.Rank() <= Severity.Rank()) return false;
        Severity = severity;
        Touch(now);
        return true;
    }

    /// <summary>Coverage of the integration degraded: condition becomes <c>unknown</c> (04 §2.1); auto-resolve timers are suspended by the caller.</summary>
    public bool MarkConditionUnknown(DateTimeOffset now)
    {
        if (!IsOpen || ConditionState != Episodes.ConditionState.Firing) return false;
        ConditionState = Episodes.ConditionState.Unknown;
        Touch(now);
        return true;
    }

    /// <summary>Coverage recovered: <c>unknown</c> → <c>firing</c>.</summary>
    public bool MarkConditionFiring(DateTimeOffset now)
    {
        if (!IsOpen || ConditionState != Episodes.ConditionState.Unknown) return false;
        ConditionState = Episodes.ConditionState.Firing;
        Touch(now);
        return true;
    }

    /// <summary>Handling: acknowledge (04 §2.2).</summary>
    public void Acknowledge(Guid actorId, DateTimeOffset now)
    {
        EnsureOpen();
        if (HandlingState == Episodes.HandlingState.Acknowledged) throw new InvalidEpisodeTransitionException("already acknowledged");
        HandlingState = Episodes.HandlingState.Acknowledged;
        AcknowledgedAt = now;
        AcknowledgedBy = actorId;
        AssigneeId ??= actorId;
        AckDeadlineAt = null;
        Touch(now);
    }

    /// <summary>Handling: manual close with a mandatory reason (04 §2.2). Condition is left as last known.</summary>
    public void CloseManually(string reason, DateTimeOffset now)
    {
        EnsureOpen();
        if (string.IsNullOrWhiteSpace(reason)) throw new InvalidEpisodeTransitionException("a reason is required to close manually");
        Close(Episodes.ClosureReason.ManualClose, Evidence.Human, reason.Trim(), now);
        Touch(now);
    }

    /// <summary>System closure with an explicit reason and evidence (auto-resolve, expiry, informational completion).</summary>
    public void CloseBySystem(string reason, string evidence, DateTimeOffset now, string? note = null)
    {
        EnsureOpen();
        if (reason == Episodes.ClosureReason.InactivityTimeout || reason == Episodes.ClosureReason.VerifiedResolved)
        {
            ConditionState = Episodes.ConditionState.Resolved;
        }
        // expired_unverified preserves the last-known condition (spec §11.2).
        Close(reason, evidence, note, now);
        Touch(now);
    }

    /// <summary>Handling: restore a closed episode for review (04 §2.2).</summary>
    public void Restore(DateTimeOffset now)
    {
        if (IsOpen) throw new InvalidEpisodeTransitionException("episode is not closed");
        if (ClosureReason is not (Episodes.ClosureReason.ExpiredUnverified or Episodes.ClosureReason.ManualClose))
        {
            throw new InvalidEpisodeTransitionException($"cannot restore an episode closed as '{ClosureReason}'");
        }
        RestoredFromReason = ClosureReason;
        ClosureReason = null;
        ResolutionEvidence = null;
        ClosureNote = null;
        ClosedAt = null;
        HandlingState = Episodes.HandlingState.Acknowledged;
        Touch(now);
    }

    public void Assign(Guid? teamId, Guid? userId, DateTimeOffset now)
    {
        EnsureOpen();
        if (teamId is not null) OwningTeamId = teamId;
        AssigneeId = userId;
        Touch(now); // deliberately no reset of ack_deadline / follow_up (04 §2.2)
    }

    private void Close(string reason, string evidence, string? note, DateTimeOffset now)
    {
        HandlingState = Episodes.HandlingState.Closed;
        ClosedAt = now;
        ClosureReason = reason;
        ResolutionEvidence = evidence;
        ClosureNote = note;
        AckDeadlineAt = null;
        FollowUpAt = null;
        AutoResolveAt = null;
    }

    private void CopyDisplayFields(NormalisedEvent evt)
    {
        Summary = evt.Summary ?? Summary;
        ResourceName = evt.ResourceName ?? ResourceName;
        RuleName = evt.RuleName ?? RuleName;
        Service = evt.Service ?? Service;
        Environment = evt.Environment ?? Environment;
        SourceUrl = evt.SourceUrl ?? SourceUrl;
        RunbookUrl = evt.RunbookUrl ?? RunbookUrl;
        SourceAlertId = evt.SourceAlertId ?? SourceAlertId;
    }

    /// <summary>Bumps the version and timestamp; every persisted change goes through here.</summary>
    public void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
    }

    private void EnsureOpen()
    {
        if (!IsOpen) throw new InvalidEpisodeTransitionException("episode is closed; closed episodes are immutable except restore and notes");
    }
}

public enum EpisodeTransitionKind
{
    Opened,
    Updated,
    Resolved,
    Cancelled,
}

/// <summary>Result of applying an effective event; the routing/notification layer decides what, if anything, to send (04 §6).</summary>
public sealed record EpisodeTransition(EpisodeTransitionKind Kind, string PreviousCondition, string NewCondition, Severity PreviousSeverity, Severity NewSeverity)
{
    /// <summary>Material severity increase (B13, 04 §6): any increase that crosses into high or critical.</summary>
    public bool IsMaterialSeverityIncrease =>
        NewSeverity.Rank() > PreviousSeverity.Rank() && NewSeverity is Severity.High or Severity.Critical or Severity.Unknown;
}

public sealed class InvalidEpisodeTransitionException(string message) : Exception(message);
