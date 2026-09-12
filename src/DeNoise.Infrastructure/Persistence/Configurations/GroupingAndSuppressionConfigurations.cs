using DeNoise.Domain.Episodes;
using DeNoise.Domain.Policies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DeNoise.Infrastructure.Persistence.Configurations;

internal sealed class AlertGroupConfiguration : IEntityTypeConfiguration<AlertGroup>
{
    public void Configure(EntityTypeBuilder<AlertGroup> b)
    {
        b.ToTable("alert_group", "alert");
        b.HasKey(x => x.GroupId);
        b.Property(x => x.GroupId).HasColumnName("group_id");
        b.Property(x => x.AccessScope).HasColumnName("access_scope");
        b.Property(x => x.RuleId).HasColumnName("rule_id");
        b.Property(x => x.KeyValues).HasColumnName("key_values").HasColumnType("jsonb");
        b.Property(x => x.OpenedAt).HasColumnName("opened_at");
        b.Property(x => x.WindowEndsAt).HasColumnName("window_ends_at");
        b.Property(x => x.Severity).HasColumnName("severity");
        b.Property(x => x.MemberCount).HasColumnName("member_count");
        b.Property(x => x.ClosedAt).HasColumnName("closed_at");
        b.HasIndex(x => new { x.RuleId, x.KeyValues }).IsUnique().HasFilter("closed_at IS NULL").HasDatabaseName("alert_group_one_open_per_key");
    }
}

internal sealed class SuppressionConfiguration : IEntityTypeConfiguration<Suppression>
{
    public void Configure(EntityTypeBuilder<Suppression> b)
    {
        b.ToTable("suppression", "cfg");
        b.HasKey(x => x.SuppressionId);
        b.Property(x => x.SuppressionId).HasColumnName("suppression_id");
        b.Property(x => x.Kind).HasColumnName("kind");
        b.Property(x => x.Name).HasColumnName("name");
        b.Property(x => x.Scope).HasColumnName("scope").HasColumnType("jsonb");
        b.Property(x => x.StartsAt).HasColumnName("starts_at");
        b.Property(x => x.EndsAt).HasColumnName("ends_at");
        b.Property(x => x.TimeZone).HasColumnName("tz");
        b.Property(x => x.Reason).HasColumnName("reason");
        b.Property(x => x.AutoPauseHeartbeats).HasColumnName("auto_pause_heartbeats");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CancelledAt).HasColumnName("cancelled_at");
        b.Property(x => x.CancelledBy).HasColumnName("cancelled_by");
        b.Property(x => x.SummarySentAt).HasColumnName("summary_sent_at");
        b.Property(x => x.StartedAt).HasColumnName("started_at");
        b.HasIndex(x => new { x.StartsAt, x.EndsAt }).HasDatabaseName("suppression_window_idx");
    }
}
