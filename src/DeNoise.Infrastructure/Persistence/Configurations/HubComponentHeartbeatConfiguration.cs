using DeNoise.Domain.Ops;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DeNoise.Infrastructure.Persistence.Configurations;

internal sealed class HubComponentHeartbeatConfiguration : IEntityTypeConfiguration<HubComponentHeartbeat>
{
    public void Configure(EntityTypeBuilder<HubComponentHeartbeat> b)
    {
        b.ToTable("hub_component_heartbeat", "ops");
        b.HasKey(x => x.Component);
        b.Property(x => x.Component).HasColumnName("component");
        b.Property(x => x.Instance).HasColumnName("instance");
        b.Property(x => x.LastSeen).HasColumnName("last_seen");
    }
}
