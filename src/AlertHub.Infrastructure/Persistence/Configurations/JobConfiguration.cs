using AlertHub.Domain.Ops;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlertHub.Infrastructure.Persistence.Configurations;

internal sealed class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> b)
    {
        b.ToTable("job", "ops");
        b.HasKey(x => x.JobId);
        b.Property(x => x.JobId).HasColumnName("job_id");
        b.Property(x => x.Kind).HasColumnName("kind");
        b.Property(x => x.Status).HasColumnName("status");
        b.Property(x => x.NotBefore).HasColumnName("not_before");
        b.Property(x => x.Priority).HasColumnName("priority").HasDefaultValue((short)100);
        b.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
        b.Property(x => x.EpisodeId).HasColumnName("episode_id");
        b.Property(x => x.IntegrationId).HasColumnName("integration_id");
        b.Property(x => x.ExpectedVersion).HasColumnName("expected_version");
        b.Property(x => x.ExpectedLastSeen).HasColumnName("expected_last_seen");
        b.Property(x => x.Attempts).HasColumnName("attempts").HasDefaultValue(0);
        b.Property(x => x.MaxAttempts).HasColumnName("max_attempts").HasDefaultValue(10);
        b.Property(x => x.ReservedBy).HasColumnName("reserved_by");
        b.Property(x => x.ReservedUntil).HasColumnName("reserved_until");
        b.Property(x => x.LastError).HasColumnName("last_error");
        b.Property(x => x.Result).HasColumnName("result").HasColumnType("jsonb");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        b.HasIndex(x => new { x.Kind, x.NotBefore }).HasDatabaseName("job_claim_idx").HasFilter("status = 'pending'");
        b.HasIndex(x => new { x.EpisodeId, x.Kind }, "job_episode_idx").HasFilter("status IN ('pending','suspended')");
        // ★ one live timer of each kind per episode (05 §3)
        b.HasIndex(x => new { x.EpisodeId, x.Kind }, "job_one_timer_per_episode").IsUnique()
            .HasFilter("status IN ('pending','reserved','suspended') AND episode_id IS NOT NULL");
    }
}
