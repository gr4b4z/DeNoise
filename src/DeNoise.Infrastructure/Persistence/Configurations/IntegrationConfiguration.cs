using DeNoise.Domain.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DeNoise.Infrastructure.Persistence.Configurations;

internal sealed class IntegrationConfiguration : IEntityTypeConfiguration<Integration>
{
    public void Configure(EntityTypeBuilder<Integration> b)
    {
        b.ToTable("integration", "cfg", t => t.HasCheckConstraint("integration_type_chk", "type IN ('azure_monitor','atlas','generic_webhook')"));
        b.HasKey(x => new { x.IntegrationId, x.Version });
        b.Property(x => x.IntegrationId).HasColumnName("integration_id");
        b.Property(x => x.Version).HasColumnName("version");
        b.Property(x => x.Name).HasColumnName("name");
        b.Property(x => x.Type).HasColumnName("type");
        b.Property(x => x.AccessScope).HasColumnName("access_scope");
        b.Property(x => x.OwnerTeamId).HasColumnName("owner_team_id");
        b.Property(x => x.IngestKeyId).HasColumnName("ingest_key_id").HasMaxLength(12);
        b.Property(x => x.IngestTokenHash).HasColumnName("ingest_token_hash");
        b.Property(x => x.HmacSecretEnc).HasColumnName("hmac_secret_enc");
        b.Property(x => x.HmacConfig).HasColumnName("hmac_config").HasColumnType("jsonb");
        b.Property(x => x.IpAllowList).HasColumnName("ip_allow_list").HasColumnType("text[]");
        b.Property(x => x.Capabilities).HasColumnName("capabilities").HasColumnType("jsonb");
        b.Property(x => x.Coverage).HasColumnName("coverage").HasColumnType("jsonb");
        b.Property(x => x.ProfileDefaults).HasColumnName("profile_defaults").HasColumnType("jsonb");
        b.Property(x => x.Active).HasColumnName("active");
        b.Property(x => x.Shadow).HasColumnName("shadow");
        b.Property(x => x.ActivatedAt).HasColumnName("activated_at");
        b.Property(x => x.DeactivatedAt).HasColumnName("deactivated_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.SourceYaml).HasColumnName("source_yaml");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Ignore(x => x.IsCurrent);

        // ★ exactly one current version per id (05 §6)
        b.HasIndex(x => x.IntegrationId, "integration_one_current_version").IsUnique().HasFilter("deactivated_at IS NULL");
        // ingest lookup: one current version per key id
        b.HasIndex(x => x.IngestKeyId, "integration_ingest_key_idx").IsUnique().HasFilter("deactivated_at IS NULL");
    }
}
