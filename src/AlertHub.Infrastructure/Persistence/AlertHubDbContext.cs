using AlertHub.Application.Mapping;
using AlertHub.Domain.Alerts;
using AlertHub.Domain.Audit;
using AlertHub.Domain.Episodes;
using AlertHub.Domain.Integrations;
using AlertHub.Domain.Notifications;
using AlertHub.Domain.Ops;
using AlertHub.Domain.Policies;
using AlertHub.Domain.Teams;
using AlertHub.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace AlertHub.Infrastructure.Persistence;

/// <summary>
/// Single EF Core context over the <c>alert</c>, <c>ops</c>, <c>audit</c>, <c>hb</c> and <c>cfg</c> schemas.
/// Hot paths (queue claim, work-queue read model) bypass EF and use hand-written SQL (ADR-4).
/// </summary>
public sealed class AlertHubDbContext(DbContextOptions<AlertHubDbContext> options) : DbContext(options)
{
    public const string MigrationsHistoryTable = "__ef_migrations_history";
    public const string MigrationsHistorySchema = "ops";

    public DbSet<RawEvent> RawEvents => Set<RawEvent>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<HubComponentHeartbeat> HubComponentHeartbeats => Set<HubComponentHeartbeat>();
    public DbSet<CoverageState> CoverageStates => Set<CoverageState>();
    public DbSet<Domain.Heartbeats.Heartbeat> Heartbeats => Set<Domain.Heartbeats.Heartbeat>();
    public DbSet<Domain.Notifications.WebhookTemplate> WebhookTemplates => Set<Domain.Notifications.WebhookTemplate>();
    public DbSet<Domain.Heartbeats.HeartbeatRun> HeartbeatRuns => Set<Domain.Heartbeats.HeartbeatRun>();
    public DbSet<Integration> Integrations => Set<Integration>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<NormalisedEvent> NormalisedEvents => Set<NormalisedEvent>();
    public DbSet<AppliedEvent> AppliedEvents => Set<AppliedEvent>();
    public DbSet<DeliveryDuplicate> DeliveryDuplicates => Set<DeliveryDuplicate>();
    public DbSet<SourceInstanceState> SourceInstanceStates => Set<SourceInstanceState>();
    public DbSet<AlertIdentity> Identities => Set<AlertIdentity>();
    public DbSet<MappingFailure> MappingFailures => Set<MappingFailure>();
    public DbSet<Episode> Episodes => Set<Episode>();
    public DbSet<EpisodeEvent> EpisodeEvents => Set<EpisodeEvent>();
    public DbSet<MappingVersion> MappingVersions => Set<MappingVersion>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<AccessScope> Scopes => Set<AccessScope>();
    public DbSet<Destination> Destinations => Set<Destination>();
    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();
    public DbSet<PolicyVersion> Policies => Set<PolicyVersion>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<PersonalAccessToken> PersonalAccessTokens => Set<PersonalAccessToken>();
    public DbSet<LoginAttempt> LoginAttempts => Set<LoginAttempt>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<SavedFilter> SavedFilters => Set<SavedFilter>();
    public DbSet<AlertGroup> AlertGroups => Set<AlertGroup>();
    public DbSet<Domain.Policies.Suppression> Suppressions => Set<Domain.Policies.Suppression>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AlertHubDbContext).Assembly);
    }
}
