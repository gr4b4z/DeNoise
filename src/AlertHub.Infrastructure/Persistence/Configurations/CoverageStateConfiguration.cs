using AlertHub.Domain.Ops;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlertHub.Infrastructure.Persistence.Configurations;

internal sealed class CoverageStateConfiguration : IEntityTypeConfiguration<CoverageState>
{
    public void Configure(EntityTypeBuilder<CoverageState> b)
    {
        b.ToTable("coverage_state", "ops");
        b.HasKey(x => x.IntegrationId);
        b.Property(x => x.IntegrationId).HasColumnName("integration_id");
        b.Property(x => x.State).HasColumnName("state");
        b.Property(x => x.Since).HasColumnName("since");
        b.Property(x => x.LastSignalAt).HasColumnName("last_signal_at");
        b.Property(x => x.ConsecutiveSuccesses).HasColumnName("consecutive_successes");
        b.Property(x => x.LastProcessedAlertAt).HasColumnName("last_processed_alert_at");
        b.Property(x => x.CoverageEpisodeId).HasColumnName("coverage_episode_id");
        b.Property(x => x.Detail).HasColumnName("detail").HasColumnType("jsonb");
    }
}
