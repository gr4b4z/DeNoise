using DeNoise.Domain.Environments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DeNoise.Infrastructure.Persistence.Configurations;

internal sealed class EnvironmentConfiguration : IEntityTypeConfiguration<EnvironmentClass>
{
    public void Configure(EntityTypeBuilder<EnvironmentClass> b)
    {
        b.ToTable("environment", "cfg", t =>
        {
            // ★ same rule as EnvironmentClasses.IsValidKey: the dictionary is matched against mapped values verbatim.
            t.HasCheckConstraint("environment_key_chk", "key ~ '^[a-z][a-z0-9_-]{0,31}$'");
            t.HasCheckConstraint("environment_label_chk", "length(btrim(label)) between 1 and 64");
        });
        b.HasKey(x => x.Key);
        b.Property(x => x.Key).HasColumnName("key");
        b.Property(x => x.Label).HasColumnName("label");
        b.Property(x => x.SortOrder).HasColumnName("sort_order");
        b.Property(x => x.IsBuiltIn).HasColumnName("is_built_in").HasDefaultValue(false);
        b.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}
