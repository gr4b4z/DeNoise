namespace AlertHub.Contracts;

// DTOs of the application API (06). camelCase on the wire; the OpenAPI document generated from AlertHub.Api is the contract.

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, string? NextCursor, int? Total);

public sealed record TeamRef(Guid Id, string Name);
public sealed record UserRef(Guid Id, string Username, string DisplayName);

public sealed record EpisodeListItem(
    Guid Id, string Severity, string? Summary, string? ResourceName, string? Service, string? Environment,
    string ConditionState, string HandlingState, TeamRef? OwningTeam, UserRef? Assignee,
    DateTimeOffset FirstSeen, DateTimeOffset LastSeen, int OccurrenceCount, bool OccurrenceCountExact,
    DateTimeOffset? AckDeadlineAt, bool AckOverdue, DateTimeOffset? NextEscalationAt, DateTimeOffset? AutoResolveAt, bool AutoResolveSuspended,
    string CoverageState, bool Stale, DateTimeOffset? SuppressedUntil, bool DeliveryFailure, Guid? GroupId, bool RoutingCorrectionRequired,
    Guid IntegrationId, string IntegrationName, string AccessScope, bool IsActionable, DateTimeOffset? ClosedAt, string? ClosureReason, string? ResolutionEvidence, int Version);

public sealed record IdentityComponentDto(string Name, string Value);
public sealed record RoutingDto(Guid? RuleId, string? RuleName, string? Why);
public sealed record ClosureDto(string Reason, string? Evidence, string? Note, DateTimeOffset? At, UserRef? By, string? RestoredFromReason);
public sealed record TimerDto(string Kind, DateTimeOffset At, string Status);
public sealed record LifecycleDto(string? Profile, int? PolicyVersion, DateTimeOffset? AutoResolveAt, bool Suspended);

public sealed record EpisodeDetail(
    EpisodeListItem Item, string? SourceAlertId, string Fingerprint, IReadOnlyList<IdentityComponentDto> IdentityComponents, LifecycleDto Lifecycle,
    RoutingDto Routing, ClosureDto? Closure, string? SourceUrl, string? RunbookUrl, bool RawPayloadAvailable, Guid? PreviousEpisodeId,
    IReadOnlyList<TimerDto> Timers, string Explanation, DateTimeOffset? AcknowledgedAt, UserRef? AcknowledgedBy, string? IdentityConfidence);

public sealed record TimelineEntry(Guid Id, DateTimeOffset At, string Kind, string Source, UserRef? Actor, Guid? EventId, string? Detail, string? DeliveryStatus, string? DestinationName);

public sealed record RelatedEpisodes(Guid? PreviousEpisodeId, Guid? NextEpisodeId, IReadOnlyList<EpisodeListItem> SameIdentity, IReadOnlyList<EpisodeListItem> GroupMembers);

public sealed record AckRequest(bool Force = false);
public sealed record AssignRequest(Guid? TeamId, Guid? UserId);
public sealed record NoteRequest(string Text);
public sealed record CloseRequest(string Reason);
public sealed record RestoreRequest(string? Reason);
public sealed record SilenceRequest(DateTimeOffset Until, string Reason);
public sealed record BulkRequest(IReadOnlyList<Guid> Ids, string Action, System.Text.Json.JsonElement? Params);
public sealed record BulkItemResult(Guid Id, bool Ok, ProblemDto? Problem);
public sealed record BulkResponse(IReadOnlyList<BulkItemResult> Results);

public sealed record ProblemDto(string Type, int Status, string Title, string? Detail);

public sealed record LoginRequest(string Username, string Password, bool KeepSignedIn = false);
public sealed record ChangePasswordRequest(string Current, string New);
public sealed record ProvidersResponse(bool Local, OidcProvider? Oidc, bool SelfServiceReset);
public sealed record OidcProvider(string DisplayName, string LoginUrl);
public sealed record CsrfResponse(string Token);

public sealed record MeResponse(Guid Id, string Username, string DisplayName, string? Email, IReadOnlyList<string> Roles, IReadOnlyList<string> Scopes,
    IReadOnlyList<string> Permissions, IReadOnlyList<TeamRef> Teams, bool MustChangePassword, string Credential);

public sealed record UserSummary(Guid Id, string Username, string DisplayName, string? Email, IReadOnlyList<string> Roles, IReadOnlyList<string> Scopes,
    string AuthProvider, bool Disabled, bool Locked, bool MustChangePassword, DateTimeOffset? LastLoginAt, DateTimeOffset CreatedAt, int Version);
public sealed record CreateUserRequest(string Username, string DisplayName, string? Email, string[] Roles, string[] Scopes);
public sealed record UpdateUserRequest(string? DisplayName, string? Email, string[]? Roles, string[]? Scopes);
public sealed record TemporaryPasswordResponse(UserSummary User, string TemporaryPassword);

public sealed record TokenSummary(Guid Id, string Name, string KeyId, IReadOnlyList<string> Scopes, DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsedAt, DateTimeOffset CreatedAt);
public sealed record CreateTokenRequest(string Name, string[] Scopes, DateTimeOffset? ExpiresAt);
public sealed record TokenCreatedResponse(TokenSummary Token, string Plaintext);
public sealed record SessionSummary(string Id, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, DateTimeOffset ExpiresAt, string? Ip, string? UserAgent, bool Current);

public sealed record TeamSummary(Guid Id, string Name, IReadOnlyList<string> AccessScopes, bool IsTriage, Guid? FallbackTeamId, Guid? DefaultEscalationPolicyId, int MemberCount, int Version);
public sealed record CreateTeamRequest(string Name, string[] AccessScopes, bool IsTriage = false, Guid? FallbackTeamId = null, Guid? DefaultEscalationPolicyId = null);
public sealed record TeamOverview(TeamSummary Team, int Unassigned, int AckOverdue, int AcknowledgedActive, int Stale, int OpenTotal, IReadOnlyList<EpisodeListItem> UnassignedItems, IReadOnlyList<EpisodeListItem> OverdueItems);

public sealed record SavedFilterDto(Guid Id, string Name, string Query);
public sealed record SaveFilterRequest(string Name, string Query);

public sealed record IntegrationSummary(Guid Id, string Name, string Type, string AccessScope, Guid? OwnerTeamId, string IngestKeyId, bool Active, bool Shadow, int Version, DateTimeOffset ActivatedAt);
public sealed record CreateIntegrationRequest(string Name, string Type, string AccessScope, Guid? OwnerTeamId);
public sealed record IntegrationCreatedResponse(IntegrationSummary Integration, string IngestPath, string IngestToken);

public sealed record PolicyVersionDto(Guid Id, string Kind, int Version, string? Name, bool Active, DateTimeOffset? ActivatedAt, DateTimeOffset? DeactivatedAt, string? CreatedBy, DateTimeOffset CreatedAt, string? Yaml);
public sealed record CreatePolicyRequest(string Yaml, Guid? PolicyId, string? Name);
public sealed record RollbackRequest(int ToVersion);

public sealed record DestinationSummary(Guid Id, string Name, string ChannelType, Guid? TeamId, string? Method, IReadOnlyList<string> EventTypes, Guid FallbackDestinationId, bool Active,
    DateTimeOffset? LastSuccessAt, DateTimeOffset? LastFailureAt, int ConsecutiveFailures, IReadOnlyList<string>? EmailTo, string? UrlMasked, int Version);
public sealed record CreateDestinationRequest(string Name, string ChannelType, Guid? TeamId, Guid? FallbackDestinationId, string? Url, string Method = "POST",
    Dictionary<string, string>? Headers = null, string? SigningSecret = null, string? Timeout = null, string[]? EventTypes = null, string[]? EmailTo = null);
public sealed record CreateDestinationPairRequest(CreateDestinationRequest Primary, CreateDestinationRequest Fallback);
public sealed record DestinationCreatedResponse(DestinationSummary Destination, string? SigningSecret);
public sealed record DestinationPairResponse(DestinationCreatedResponse Primary, DestinationCreatedResponse Fallback);
public sealed record DeliveryAttemptDto(Guid Id, Guid OutboxId, DateTimeOffset AttemptedAt, string Channel, string Outcome, int? HttpStatus, int LatencyMs, string? Error, bool UsedFallback, string? ResponseExcerpt);

public sealed record ChangeEventDto(string Type, long Id, string Scope, System.Text.Json.JsonElement Data);

// --- Milestone 5: lifecycle impact, integration health, hub health ---
public sealed record PolicyImpactSampleDto(Guid EpisodeId, string? Summary, string Severity, DateTimeOffset LastSeen, DateTimeOffset? CurrentAutoResolveAt, DateTimeOffset? ProposedAutoResolveAt, string Note);
public sealed record PolicyImpactDto(string Kind, Guid PolicyId, int Version, int AffectedOpenEpisodes, IReadOnlyList<PolicyImpactSampleDto> Sample, string Explanation);
public sealed record IntegrationHealth(Guid IntegrationId, string Name, bool CoverageConfigured, string CoverageState, DateTimeOffset? CoverageSince, DateTimeOffset? LastSignalAt, int ConsecutiveSuccesses,
    Guid? CoverageEpisodeId, DateTimeOffset? LastProcessedAlertAt, int AcceptedLast15m, int MappingFailuresLast15m, int PendingNormalise, DateTimeOffset? OldestPendingSince, int SuspendedAutoResolve, int OpenEpisodes);
public sealed record HubComponent(string Component, string Instance, DateTimeOffset LastSeen, bool Healthy);
public sealed record QueueDepth(string Kind, int Pending, int Reserved, int Suspended, int Failed, DateTimeOffset? OldestPending);
public sealed record DeadmanStatus(bool Configured, DateTimeOffset? LastPingAt, bool Ok);
public sealed record HubHealth(DateTimeOffset At, bool DatabaseOk, IReadOnlyList<HubComponent> Components, IReadOnlyList<QueueDepth> Queues, int OutboxPending, DateTimeOffset? OutboxOldestPending, int OutboxFailed, int JobsFailed, DeadmanStatus Deadman);
public sealed record HubFailure(Guid Id, string Source, string Type, DateTimeOffset At, int Attempts, string? LastError, Guid? EpisodeId);
