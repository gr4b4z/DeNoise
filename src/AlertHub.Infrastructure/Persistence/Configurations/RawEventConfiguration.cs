using AlertHub.Domain.Alerts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlertHub.Infrastructure.Persistence.Configurations;

internal sealed class RawEventConfiguration : IEntityTypeConfiguration<RawEvent>
{
    public void Configure(EntityTypeBuilder<RawEvent> b)
    {
        // The table itself is created by hand-written SQL in the initial migration (PARTITION BY RANGE);
        // this mapping only describes the shape for reads and inserts.
        b.ToTable("raw_event", "alert");
        b.HasKey(x => new { x.ReceivedAt, x.EventId });
        b.Property(x => x.EventId).HasColumnName("event_id");
        b.Property(x => x.IntegrationId).HasColumnName("integration_id");
        b.Property(x => x.ReceivedAt).HasColumnName("received_at");
        b.Property(x => x.ContentType).HasColumnName("content_type");
        b.Property(x => x.Body).HasColumnName("body");
        b.Property(x => x.Headers).HasColumnName("headers").HasColumnType("jsonb");
        b.Property(x => x.SourceIp).HasColumnName("source_ip").HasColumnType("inet");
        b.Property(x => x.SizeBytes).HasColumnName("size_bytes");
    }
}
