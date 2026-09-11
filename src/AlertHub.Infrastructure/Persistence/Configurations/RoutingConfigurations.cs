using AlertHub.Domain.Notifications;
using AlertHub.Domain.Policies;
using AlertHub.Domain.Teams;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlertHub.Infrastructure.Persistence.Configurations;

internal sealed class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    public void Configure(EntityTypeBuilder<Team> b)
    {
        b.ToTable("team", "cfg");
        b.HasKey(x => x.TeamId);
        b.Property(x => x.TeamId).HasColumnName("team_id");
        b.Property(x => x.Name).HasColumnName("name");
        b.Property(x => x.AccessScopes).HasColumnName("access_scopes").HasColumnType("text[]");
        b.Property(x => x.FallbackTeamId).HasColumnName("fallback_team_id");
        b.Property(x => x.CoverageHours).HasColumnName("coverage_hours").HasColumnType("jsonb");
        b.Property(x => x.DefaultEscalationPolicyId).HasColumnName("default_escalation_policy_id");
        b.Property(x => x.EntraGroupIds).HasColumnName("entra_group_ids").HasColumnType("text[]");
        b.Property(x => x.IsTriage).HasColumnName("is_triage").HasDefaultValue(false);
        b.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.HasIndex(x => x.Name, "team_name_uq").IsUnique();
        // ★ at most one triage team
        b.HasIndex(x => x.IsTriage, "team_one_triage").IsUnique().HasFilter("is_triage");
    }
}

internal sealed class AccessScopeConfiguration : IEntityTypeConfiguration<AccessScope>
{
    public void Configure(EntityTypeBuilder<AccessScope> b)
    {
        b.ToTable("scope", "cfg");
        b.HasKey(x => x.Scope);
        b.Property(x => x.Scope).HasColumnName("scope");
        b.Property(x => x.Product).HasColumnName("product");
        b.Property(x => x.EntraGroupIds).HasColumnName("entra_group_ids").HasColumnType("text[]");
    }
}

internal sealed class DestinationConfiguration : IEntityTypeConfiguration<Destination>
{
    public void Configure(EntityTypeBuilder<Destination> b)
    {
        b.ToTable("destination", "cfg", t =>
        {
            // ★ fallback mandatory and never self (05 §6)
            t.HasCheckConstraint("destination_fallback_not_self_chk", "fallback_destination_id <> destination_id");
            t.HasCheckConstraint("destination_channel_chk", "channel_type IN ('webhook','smtp_email')");
        });
        b.HasKey(x => x.DestinationId);
        b.Property(x => x.DestinationId).HasColumnName("destination_id");
        b.Property(x => x.TeamId).HasColumnName("team_id");
        b.Property(x => x.Name).HasColumnName("name");
        b.Property(x => x.ChannelType).HasColumnName("channel_type");
        b.Property(x => x.UrlEnc).HasColumnName("url_enc");
        b.Property(x => x.Method).HasColumnName("method").HasDefaultValue("POST");
        b.Property(x => x.HeadersEnc).HasColumnName("headers_enc");
        b.Property(x => x.BodyTemplateId).HasColumnName("body_template_id");
        b.Property(x => x.SigningSecretEnc).HasColumnName("signing_secret_enc");
        b.Property(x => x.Timeout).HasColumnName("timeout").HasColumnType("interval");
        b.Property(x => x.EventTypes).HasColumnName("event_types").HasColumnType("text[]");
        b.Property(x => x.RetryPolicy).HasColumnName("retry_policy").HasColumnType("jsonb");
        b.Property(x => x.EmailTo).HasColumnName("email_to").HasColumnType("text[]");
        b.Property(x => x.FallbackDestinationId).HasColumnName("fallback_destination_id");
        b.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
        b.Property(x => x.Active).HasColumnName("active").HasDefaultValue(true);
        b.Property(x => x.LastSuccessAt).HasColumnName("last_success_at");
        b.Property(x => x.LastFailureAt).HasColumnName("last_failure_at");
        b.Property(x => x.ConsecutiveFailures).HasColumnName("consecutive_failures").HasDefaultValue(0);
        b.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).HasConstraintName("destination_team_fk").OnDelete(DeleteBehavior.Restrict);
        // The fallback FK is deferrable so a bootstrap pair can be inserted in one transaction.
        b.HasIndex(x => x.TeamId, "destination_team_idx");
    }
}

internal sealed class DeliveryAttemptConfiguration : IEntityTypeConfiguration<DeliveryAttempt>
{
    public void Configure(EntityTypeBuilder<DeliveryAttempt> b)
    {
        b.ToTable("delivery_attempt", "ops");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.OutboxId).HasColumnName("outbox_id");
        b.Property(x => x.AttemptedAt).HasColumnName("attempted_at");
        b.Property(x => x.Channel).HasColumnName("channel");
        b.Property(x => x.Outcome).HasColumnName("outcome");
        b.Property(x => x.HttpStatus).HasColumnName("http_status");
        b.Property(x => x.LatencyMs).HasColumnName("latency_ms");
        b.Property(x => x.Error).HasColumnName("error");
        b.Property(x => x.UsedFallback).HasColumnName("used_fallback").HasDefaultValue(false);
        b.Property(x => x.ResponseExcerpt).HasColumnName("response_excerpt");
        b.HasIndex(x => x.AttemptedAt, "delivery_attempt_at_idx");
        b.HasIndex(x => x.OutboxId, "delivery_attempt_outbox_idx");
    }
}

internal sealed class PolicyVersionConfiguration : IEntityTypeConfiguration<PolicyVersion>
{
    public void Configure(EntityTypeBuilder<PolicyVersion> b)
    {
        b.ToTable("policy", "cfg", t => t.HasCheckConstraint("policy_kind_chk", "kind IN ('routing','escalation','lifecycle','grouping')"));
        b.HasKey(x => new { x.Kind, x.PolicyId, x.Version });
        b.Property(x => x.Kind).HasColumnName("kind");
        b.Property(x => x.PolicyId).HasColumnName("policy_id");
        b.Property(x => x.Version).HasColumnName("version");
        b.Property(x => x.Name).HasColumnName("name");
        b.Property(x => x.Body).HasColumnName("body").HasColumnType("jsonb");
        b.Property(x => x.SourceYaml).HasColumnName("source_yaml");
        b.Property(x => x.ActivatedAt).HasColumnName("activated_at");
        b.Property(x => x.DeactivatedAt).HasColumnName("deactivated_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Ignore(x => x.IsActive);
        // ★ one active version per (kind, id)
        b.HasIndex(x => new { x.Kind, x.PolicyId }, "policy_one_active_version").IsUnique().HasFilter("activated_at IS NOT NULL AND deactivated_at IS NULL");
    }
}
