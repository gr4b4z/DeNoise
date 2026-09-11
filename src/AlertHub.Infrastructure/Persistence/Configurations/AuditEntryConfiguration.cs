using AlertHub.Domain.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlertHub.Infrastructure.Persistence.Configurations;

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> b)
    {
        b.ToTable("entry", "audit");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.At).HasColumnName("at");
        b.Property(x => x.ActorType).HasColumnName("actor_type");
        b.Property(x => x.ActorId).HasColumnName("actor_id");
        b.Property(x => x.ActorDisplay).HasColumnName("actor_display");
        b.Property(x => x.Action).HasColumnName("action");
        b.Property(x => x.TargetType).HasColumnName("target_type");
        b.Property(x => x.TargetId).HasColumnName("target_id");
        b.Property(x => x.AccessScope).HasColumnName("access_scope");
        b.Property(x => x.Before).HasColumnName("before").HasColumnType("jsonb");
        b.Property(x => x.After).HasColumnName("after").HasColumnType("jsonb");
        b.Property(x => x.Reason).HasColumnName("reason");
        b.Property(x => x.CorrelationId).HasColumnName("correlation_id");
        b.Property(x => x.RequestIp).HasColumnName("request_ip").HasColumnType("inet");
        b.HasIndex(x => new { x.TargetType, x.TargetId, x.At }, "audit_target_idx").IsDescending(false, false, true);
        b.HasIndex(x => new { x.ActorId, x.At }, "audit_actor_idx").IsDescending(false, true);
        b.HasIndex(x => x.At, "audit_at_idx");
    }
}
