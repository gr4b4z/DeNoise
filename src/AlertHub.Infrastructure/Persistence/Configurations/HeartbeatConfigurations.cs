using AlertHub.Domain.Heartbeats;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlertHub.Infrastructure.Persistence.Configurations;

internal sealed class HeartbeatConfiguration : IEntityTypeConfiguration<Heartbeat>
{
    public void Configure(EntityTypeBuilder<Heartbeat> b)
    {
        b.ToTable("heartbeat", "hb");
        b.HasKey(x => x.HeartbeatId);
        b.Property(x => x.HeartbeatId).HasColumnName("heartbeat_id");
        b.Property(x => x.Name).HasColumnName("name");
        b.Property(x => x.Description).HasColumnName("description");
        b.Property(x => x.AccessScope).HasColumnName("access_scope");
        b.Property(x => x.OwningTeamId).HasColumnName("owning_team_id");
        b.Property(x => x.AssigneeId).HasColumnName("assignee_id");
        b.Property(x => x.ScheduleKind).HasColumnName("schedule_kind");
        b.Property(x => x.Interval).HasColumnName("interval");
        b.Property(x => x.Cron).HasColumnName("cron");
        b.Property(x => x.ScheduleTz).HasColumnName("schedule_tz");
        b.Property(x => x.Grace).HasColumnName("grace");
        b.Property(x => x.SeverityOnMiss).HasColumnName("severity_on_miss");
        b.Property(x => x.RoutingPolicyId).HasColumnName("routing_policy_id");
        b.Property(x => x.BindsToIntegrationId).HasColumnName("binds_to_integration_id");
        b.Property(x => x.RecoverySuccessesRequired).HasColumnName("recovery_successes_required");
        b.Property(x => x.AutoPauseDuringMaintenance).HasColumnName("auto_pause_during_maintenance");
        b.Property(x => x.State).HasColumnName("state");
        b.Property(x => x.ExpectedNext).HasColumnName("expected_next");
        b.Property(x => x.LastPingAt).HasColumnName("last_ping_at");
        b.Property(x => x.LastPingIp).HasColumnName("last_ping_ip");
        b.Property(x => x.LastRunDuration).HasColumnName("last_run_duration");
        b.Property(x => x.RunStartedAt).HasColumnName("run_started_at");
        b.Property(x => x.ConsecutiveSuccesses).HasColumnName("consecutive_successes");
        b.Property(x => x.PausedBy).HasColumnName("paused_by");
        b.Property(x => x.PausedAt).HasColumnName("paused_at");
        b.Property(x => x.PauseReason).HasColumnName("pause_reason");
        b.Property(x => x.PausedByMaintenance).HasColumnName("paused_by_maintenance");
        b.Property(x => x.KeyId).HasColumnName("key_id");
        b.Property(x => x.TokenHash).HasColumnName("token_hash");
        b.Property(x => x.TokenRotatedAt).HasColumnName("token_rotated_at");
        b.Property(x => x.MissEpisodeId).HasColumnName("miss_episode_id");
        b.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.HasIndex(x => x.KeyId, "hb_key_idx").IsUnique();
        b.HasIndex(x => x.ExpectedNext, "hb_due_idx").HasFilter("state IN ('healthy','late')");
        b.HasIndex(x => new { x.OwningTeamId, x.State }, "hb_team_idx");
        b.HasIndex(x => x.BindsToIntegrationId, "hb_binding_idx");
    }
}

internal sealed class HeartbeatRunConfiguration : IEntityTypeConfiguration<HeartbeatRun>
{
    public void Configure(EntityTypeBuilder<HeartbeatRun> b)
    {
        b.ToTable("run", "hb");
        b.HasKey(x => new { x.HeartbeatId, x.Seq });
        b.Property(x => x.HeartbeatId).HasColumnName("heartbeat_id");
        b.Property(x => x.Seq).HasColumnName("seq");
        b.Property(x => x.StartedAt).HasColumnName("started_at");
        b.Property(x => x.FinishedAt).HasColumnName("finished_at");
        b.Property(x => x.Kind).HasColumnName("kind");
        b.Property(x => x.ExitCode).HasColumnName("exit_code");
        b.Property(x => x.Body).HasColumnName("body");
        b.Property(x => x.SourceIp).HasColumnName("source_ip");
    }
}
