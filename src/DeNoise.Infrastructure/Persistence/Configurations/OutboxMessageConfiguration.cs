using DeNoise.Domain.Ops;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DeNoise.Infrastructure.Persistence.Configurations;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> b)
    {
        b.ToTable("outbox", "ops");
        b.HasKey(x => x.OutboxId);
        b.Property(x => x.OutboxId).HasColumnName("outbox_id");
        b.Property(x => x.EpisodeId).HasColumnName("episode_id");
        b.Property(x => x.Type).HasColumnName("type");
        b.Property(x => x.DestinationId).HasColumnName("destination_id");
        b.Property(x => x.PolicyVersion).HasColumnName("policy_version");
        b.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
        b.Property(x => x.Status).HasColumnName("status");
        b.Property(x => x.NotBefore).HasColumnName("not_before");
        b.Property(x => x.Attempts).HasColumnName("attempts").HasDefaultValue(0);
        b.Property(x => x.ReservedBy).HasColumnName("reserved_by");
        b.Property(x => x.ReservedUntil).HasColumnName("reserved_until");
        b.Property(x => x.SentAt).HasColumnName("sent_at");
        b.Property(x => x.LastError).HasColumnName("last_error");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");

        b.HasIndex(x => x.NotBefore).HasDatabaseName("outbox_claim_idx").HasFilter("status = 'pending'");
        b.HasIndex(x => new { x.EpisodeId, x.CreatedAt }).HasDatabaseName("outbox_episode_idx");
    }
}
