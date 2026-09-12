using System.Text.Json;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Mapping;
using DeNoise.Domain.Alerts;
using DeNoise.Domain.Common;
using DeNoise.Domain.Episodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DeNoise.Infrastructure.Persistence.Configurations;

/// <summary>Shared converters: fingerprints are hex strings in the domain and <c>bytea</c> in the database; small maps are <c>jsonb</c>.</summary>
internal static class Converters
{
    public static readonly ValueConverter<string, byte[]> HexToBytes = new(hex => Fingerprint.ToBytes(hex), bytes => Fingerprint.ToHex(bytes));
    public static readonly ValueConverter<string?, byte[]?> NullableHexToBytes = new(hex => hex == null ? null : Fingerprint.ToBytes(hex), bytes => bytes == null ? null : Fingerprint.ToHex(bytes));

    public static readonly ValueConverter<IReadOnlyDictionary<string, string>?, string?> MapToJson = new(
        map => map == null ? null : JsonSerializer.Serialize(map, JsonDefaults.Stored),
        json => json == null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonDefaults.Stored));

    public static readonly ValueComparer<IReadOnlyDictionary<string, string>?> MapComparer = new(
        (a, b) => MapsEqual(a, b),
        m => m == null ? 0 : m.OrderBy(kv => kv.Key, StringComparer.Ordinal).Aggregate(17, (h, kv) => HashCode.Combine(h, kv.Key, kv.Value)),
        m => m == null ? null : new Dictionary<string, string>(m));

    private static bool MapsEqual(IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string>? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Count != b.Count) return false;
        foreach (var (k, v) in a)
        {
            if (!b.TryGetValue(k, out var other) || other != v) return false;
        }
        return true;
    }

    public static readonly ValueConverter<IReadOnlyList<IdentityComponent>?, string?> ComponentsToJson = new(
        list => list == null ? null : JsonSerializer.Serialize(list, JsonDefaults.Stored),
        json => json == null ? null : JsonSerializer.Deserialize<List<IdentityComponent>>(json, JsonDefaults.Stored));

    public static readonly ValueComparer<IReadOnlyList<IdentityComponent>?> ComponentsComparer = new(
        (a, b) => (a == null && b == null) || (a != null && b != null && a.SequenceEqual(b)),
        l => l == null ? 0 : l.Aggregate(17, (h, c) => HashCode.Combine(h, c)),
        l => l == null ? null : l.ToList());

    public static readonly ValueConverter<Severity, string> SeverityToWire = new(s => s.ToWire(), s => SeverityExtensions.ParseWire(s));
}

internal sealed class NormalisedEventConfiguration : IEntityTypeConfiguration<NormalisedEvent>
{
    public void Configure(EntityTypeBuilder<NormalisedEvent> b)
    {
        b.ToTable("normalised_event", "alert");
        b.HasKey(x => x.EventId);
        b.Property(x => x.EventId).HasColumnName("event_id");
        b.Property(x => x.IntegrationId).HasColumnName("integration_id");
        b.Property(x => x.RawReceivedAt).HasColumnName("raw_received_at");
        b.Property(x => x.MappingVersion).HasColumnName("mapping_version");
        b.Property(x => x.EventType).HasColumnName("event_type");
        b.Property(x => x.SourceAlertId).HasColumnName("source_alert_id");
        b.Property(x => x.SourceEventId).HasColumnName("source_event_id");
        b.Property(x => x.SourceVersion).HasColumnName("source_version");
        b.Property(x => x.OccurredAt).HasColumnName("occurred_at");
        b.Property(x => x.ReceivedAt).HasColumnName("received_at");
        b.Property(x => x.Severity).HasColumnName("severity").HasConversion(Converters.SeverityToWire);
        b.Property(x => x.SourceSeverity).HasColumnName("source_severity");
        b.Property(x => x.ResourceId).HasColumnName("resource_id");
        b.Property(x => x.ResourceName).HasColumnName("resource_name");
        b.Property(x => x.RuleId).HasColumnName("rule_id");
        b.Property(x => x.RuleName).HasColumnName("rule_name");
        b.Property(x => x.Environment).HasColumnName("environment");
        b.Property(x => x.Service).HasColumnName("service");
        b.Property(x => x.Summary).HasColumnName("summary");
        b.Property(x => x.SourceUrl).HasColumnName("source_url");
        b.Property(x => x.RunbookUrl).HasColumnName("runbook_url");
        b.Property(x => x.Dimensions).HasColumnName("dimensions").HasColumnType("jsonb").HasConversion(Converters.MapToJson, Converters.MapComparer);
        b.Property(x => x.Labels).HasColumnName("labels").HasColumnType("jsonb").HasConversion(Converters.MapToJson, Converters.MapComparer);
        b.Property(x => x.Fingerprint).HasColumnName("fingerprint").HasConversion(Converters.NullableHexToBytes);
        b.Property(x => x.IdentityComponents).HasColumnName("identity_components").HasColumnType("jsonb").HasConversion(Converters.ComponentsToJson, Converters.ComponentsComparer);
        b.Property(x => x.DeliveryKey).HasColumnName("delivery_key").HasMaxLength(DeliveryKey.MaxLength);
        b.Property(x => x.IdentityConfidence).HasColumnName("identity_confidence");
        b.Property(x => x.LifecycleProfileHint).HasColumnName("lifecycle_profile_hint");
        b.Property(x => x.EpisodeId).HasColumnName("episode_id");
        b.Ignore(x => x.EffectiveAt);
        b.Ignore(x => x.IsActionableType);
        b.HasIndex(x => new { x.IntegrationId, x.ReceivedAt }, "normalised_event_integration_idx").IsDescending(false, true);
        b.HasIndex(x => x.EpisodeId, "normalised_event_episode_idx");
    }
}

internal sealed class AppliedEventConfiguration : IEntityTypeConfiguration<AppliedEvent>
{
    public void Configure(EntityTypeBuilder<AppliedEvent> b)
    {
        b.ToTable("applied_event", "alert");
        b.HasKey(x => new { x.IntegrationId, x.DeliveryKey }); // ★ duplicates rejected here, nowhere else
        b.Property(x => x.IntegrationId).HasColumnName("integration_id");
        b.Property(x => x.DeliveryKey).HasColumnName("delivery_key").HasMaxLength(DeliveryKey.MaxLength);
        b.Property(x => x.EventId).HasColumnName("event_id");
        b.Property(x => x.AppliedAt).HasColumnName("applied_at");
        b.Property(x => x.Outcome).HasColumnName("outcome");
        b.HasIndex(x => x.EventId, "applied_event_event_idx");
    }
}

internal sealed class DeliveryDuplicateConfiguration : IEntityTypeConfiguration<DeliveryDuplicate>
{
    public void Configure(EntityTypeBuilder<DeliveryDuplicate> b)
    {
        b.ToTable("delivery_duplicate", "alert");
        b.HasKey(x => new { x.IntegrationId, x.DeliveryKey });
        b.Property(x => x.IntegrationId).HasColumnName("integration_id");
        b.Property(x => x.DeliveryKey).HasColumnName("delivery_key").HasMaxLength(DeliveryKey.MaxLength);
        b.Property(x => x.Count).HasColumnName("count");
        b.Property(x => x.FirstSeen).HasColumnName("first_seen");
        b.Property(x => x.LastSeen).HasColumnName("last_seen");
    }
}

internal sealed class SourceInstanceStateConfiguration : IEntityTypeConfiguration<SourceInstanceState>
{
    public void Configure(EntityTypeBuilder<SourceInstanceState> b)
    {
        b.ToTable("source_instance_state", "alert");
        b.HasKey(x => new { x.IntegrationId, x.SourceAlertId });
        b.Property(x => x.IntegrationId).HasColumnName("integration_id");
        b.Property(x => x.SourceAlertId).HasColumnName("source_alert_id");
        b.Property(x => x.ResolvedAt).HasColumnName("resolved_at");
    }
}

internal sealed class AlertIdentityConfiguration : IEntityTypeConfiguration<AlertIdentity>
{
    public void Configure(EntityTypeBuilder<AlertIdentity> b)
    {
        b.ToTable("identity", "alert");
        b.HasKey(x => x.Fingerprint);
        b.Property(x => x.Fingerprint).HasColumnName("fingerprint").HasConversion(Converters.HexToBytes);
        b.Property(x => x.IntegrationId).HasColumnName("integration_id");
        b.Property(x => x.AccessScope).HasColumnName("access_scope");
        b.Property(x => x.IdentityVersion).HasColumnName("identity_version");
        b.Property(x => x.Components).HasColumnName("components").HasColumnType("jsonb");
        b.Property(x => x.FirstSeen).HasColumnName("first_seen");
        b.Property(x => x.EpisodeCount).HasColumnName("episode_count").HasDefaultValue(0);
    }
}

internal sealed class MappingFailureConfiguration : IEntityTypeConfiguration<MappingFailure>
{
    public void Configure(EntityTypeBuilder<MappingFailure> b)
    {
        b.ToTable("mapping_failure", "alert");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.IntegrationId).HasColumnName("integration_id");
        b.Property(x => x.EventId).HasColumnName("event_id");
        b.Property(x => x.RawReceivedAt).HasColumnName("raw_received_at");
        b.Property(x => x.MappingVersion).HasColumnName("mapping_version");
        b.Property(x => x.Error).HasColumnName("error");
        b.Property(x => x.Field).HasColumnName("field");
        b.Property(x => x.Quarantined).HasColumnName("quarantined").HasDefaultValue(true);
        b.Property(x => x.ResolvedAt).HasColumnName("resolved_at");
        b.Property(x => x.ResolvedBy).HasColumnName("resolved_by");
        b.HasIndex(x => new { x.IntegrationId, x.RawReceivedAt }, "mapping_failure_integration_idx").HasFilter("quarantined");
    }
}

internal sealed class EpisodeConfiguration : IEntityTypeConfiguration<Episode>
{
    public void Configure(EntityTypeBuilder<Episode> b)
    {
        b.ToTable("episode", "alert");
        b.HasKey(x => x.EpisodeId);
        b.Property(x => x.EpisodeId).HasColumnName("episode_id");
        b.Property(x => x.Fingerprint).HasColumnName("fingerprint").HasConversion(Converters.HexToBytes);
        b.Property(x => x.IntegrationId).HasColumnName("integration_id");
        b.Property(x => x.AccessScope).HasColumnName("access_scope");
        b.Property(x => x.PreviousEpisodeId).HasColumnName("previous_episode_id");
        b.Property(x => x.ConditionState).HasColumnName("condition_state");
        b.Property(x => x.HandlingState).HasColumnName("handling_state");
        b.Property(x => x.Severity).HasColumnName("severity").HasConversion(Converters.SeverityToWire);
        b.Property(x => x.IsActionable).HasColumnName("is_actionable");
        b.Property(x => x.FirstSeen).HasColumnName("first_seen");
        b.Property(x => x.LastSeen).HasColumnName("last_seen");
        b.Property(x => x.LastReceivedAt).HasColumnName("last_received_at");
        b.Property(x => x.LastVerifiedAt).HasColumnName("last_verified_at");
        b.Property(x => x.LastAppliedVersion).HasColumnName("last_applied_version");
        b.Property(x => x.OccurrenceCount).HasColumnName("occurrence_count").HasDefaultValue(1);
        b.Property(x => x.OwningTeamId).HasColumnName("owning_team_id");
        b.Property(x => x.AssigneeId).HasColumnName("assignee_id");
        b.Property(x => x.RoutingRuleId).HasColumnName("routing_rule_id");
        b.Property(x => x.RoutingCorrectionRequired).HasColumnName("routing_correction_required").HasDefaultValue(false);
        b.Property(x => x.AckDeadlineAt).HasColumnName("ack_deadline_at");
        b.Property(x => x.AcknowledgedAt).HasColumnName("acknowledged_at");
        b.Property(x => x.AcknowledgedBy).HasColumnName("acknowledged_by");
        b.Property(x => x.FollowUpAt).HasColumnName("follow_up_at");
        b.Property(x => x.AutoResolveAt).HasColumnName("auto_resolve_at");
        b.Property(x => x.LifecyclePolicyVersion).HasColumnName("lifecycle_policy_version");
        b.Property(x => x.LifecycleProfile).HasColumnName("lifecycle_profile");
        b.Property(x => x.LifecyclePolicyId).HasColumnName("lifecycle_policy_id");
        b.Property(x => x.StaleSince).HasColumnName("stale_since");
        b.Property(x => x.ClosedAt).HasColumnName("closed_at");
        b.Property(x => x.ClosureReason).HasColumnName("closure_reason");
        b.Property(x => x.ResolutionEvidence).HasColumnName("resolution_evidence");
        b.Property(x => x.ClosureNote).HasColumnName("closure_note");
        b.Property(x => x.RestoredFromReason).HasColumnName("restored_from_reason");
        b.Property(x => x.SuppressedUntil).HasColumnName("suppressed_until");
        b.Property(x => x.SuppressionSource).HasColumnName("suppression_source");
        b.Property(x => x.GroupId).HasColumnName("group_id");
        b.Property(x => x.Summary).HasColumnName("summary");
        b.Property(x => x.ResourceName).HasColumnName("resource_name");
        b.Property(x => x.RuleName).HasColumnName("rule_name");
        b.Property(x => x.Service).HasColumnName("service");
        b.Property(x => x.Environment).HasColumnName("environment");
        b.Property(x => x.SourceUrl).HasColumnName("source_url");
        b.Property(x => x.RunbookUrl).HasColumnName("runbook_url");
        b.Property(x => x.SourceAlertId).HasColumnName("source_alert_id");
        b.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Ignore(x => x.IsOpen);

        b.HasOne<AlertIdentity>().WithMany().HasForeignKey(x => x.Fingerprint).HasConstraintName("episode_identity_fk").OnDelete(DeleteBehavior.Restrict);

        // ★ exactly one open episode per identity (05 §2)
        b.HasIndex(x => x.Fingerprint, "episode_one_open_per_identity").IsUnique().HasFilter("handling_state <> 'closed'");
        b.HasIndex(x => new { x.AccessScope, x.HandlingState, x.Severity, x.LastSeen }, "episode_queue_idx").IsDescending(false, false, false, true).HasFilter("handling_state <> 'closed'");
        b.HasIndex(x => new { x.OwningTeamId, x.HandlingState, x.AckDeadlineAt }, "episode_team_idx");
        b.HasIndex(x => x.ClosedAt, "episode_closed_idx").HasFilter("handling_state = 'closed'");
        b.HasIndex(x => new { x.IntegrationId, x.HandlingState }, "episode_integration_idx");
        // search_tsv (generated tsvector) and its GIN index are created by SQL in the migration; EF does not map them.
    }
}

internal sealed class EpisodeEventConfiguration : IEntityTypeConfiguration<EpisodeEvent>
{
    public void Configure(EntityTypeBuilder<EpisodeEvent> b)
    {
        b.ToTable("episode_event", "alert");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.EpisodeId).HasColumnName("episode_id");
        b.Property(x => x.At).HasColumnName("at");
        b.Property(x => x.Kind).HasColumnName("kind");
        b.Property(x => x.ActorId).HasColumnName("actor_id");
        b.Property(x => x.EventId).HasColumnName("event_id");
        b.Property(x => x.Detail).HasColumnName("detail").HasColumnType("jsonb");
        b.HasOne<Episode>().WithMany().HasForeignKey(x => x.EpisodeId).HasConstraintName("episode_event_episode_fk").OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.EpisodeId, x.At }, "episode_event_episode_idx");
    }
}

internal sealed class MappingVersionConfiguration : IEntityTypeConfiguration<MappingVersion>
{
    public void Configure(EntityTypeBuilder<MappingVersion> b)
    {
        b.ToTable("mapping", "cfg");
        b.HasKey(x => new { x.MappingId, x.Version });
        b.Property(x => x.MappingId).HasColumnName("mapping_id");
        b.Property(x => x.Version).HasColumnName("version");
        b.Property(x => x.IntegrationId).HasColumnName("integration_id");
        b.Property(x => x.Order).HasColumnName("evaluation_order").HasDefaultValue(100);
        b.Property(x => x.Name).HasColumnName("name");
        b.Property(x => x.Body).HasColumnName("body").HasColumnType("jsonb");
        b.Property(x => x.SourceYaml).HasColumnName("source_yaml");
        b.Property(x => x.IdentityVersion).HasColumnName("identity_version");
        b.Property(x => x.Samples).HasColumnName("samples").HasColumnType("jsonb");
        b.Property(x => x.ActivatedAt).HasColumnName("activated_at");
        b.Property(x => x.DeactivatedAt).HasColumnName("deactivated_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Ignore(x => x.IsActive);
        // ★ at most one active version per mapping id
        b.HasIndex(x => x.MappingId, "mapping_one_active_version").IsUnique().HasFilter("activated_at IS NOT NULL AND deactivated_at IS NULL");
        b.HasIndex(x => new { x.IntegrationId, x.Order }, "mapping_integration_idx");
    }
}
