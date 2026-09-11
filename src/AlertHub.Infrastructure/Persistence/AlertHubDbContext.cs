using AlertHub.Application.Mapping;
using AlertHub.Domain.Alerts;
using AlertHub.Domain.Audit;
using AlertHub.Domain.Episodes;
using AlertHub.Domain.Integrations;
using AlertHub.Domain.Ops;
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AlertHubDbContext).Assembly);
    }
}
