using DeNoise.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DeNoise.Infrastructure.Persistence.Configurations;

internal sealed class WebhookTemplateConfiguration : IEntityTypeConfiguration<WebhookTemplate>
{
    public void Configure(EntityTypeBuilder<WebhookTemplate> b)
    {
        b.ToTable("webhook_template", "cfg");
        b.HasKey(x => new { x.TemplateId, x.Version });
        b.Property(x => x.TemplateId).HasColumnName("template_id");
        b.Property(x => x.Version).HasColumnName("version");
        b.Property(x => x.Name).HasColumnName("name");
        b.Property(x => x.Description).HasColumnName("description");
        b.Property(x => x.Format).HasColumnName("format");
        b.Property(x => x.Body).HasColumnName("body");
        b.Property(x => x.ContentType).HasColumnName("content_type");
        b.Property(x => x.SampleOutput).HasColumnName("sample_output");
        b.Property(x => x.Builtin).HasColumnName("builtin");
        b.Property(x => x.ActivatedAt).HasColumnName("activated_at");
        b.Property(x => x.DeactivatedAt).HasColumnName("deactivated_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.HasIndex(x => x.TemplateId, "webhook_template_one_active").IsUnique().HasFilter("activated_at IS NOT NULL AND deactivated_at IS NULL");
    }
}
