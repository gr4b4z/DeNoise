using DeNoise.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DeNoise.Infrastructure.Persistence.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("user", "cfg", t => t.HasCheckConstraint("user_provider_chk", "auth_provider IN ('local','oidc')"));
        b.HasKey(x => x.UserId);
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.Username).HasColumnName("username").HasMaxLength(64);
        b.Property(x => x.Email).HasColumnName("email").HasMaxLength(320);
        b.Property(x => x.DisplayName).HasColumnName("display_name");
        b.Property(x => x.AuthProvider).HasColumnName("auth_provider").HasDefaultValue(AuthProviders.Local);
        b.Property(x => x.ExternalId).HasColumnName("external_id");
        b.Property(x => x.PasswordHash).HasColumnName("password_hash");
        b.Property(x => x.PasswordChangedAt).HasColumnName("password_changed_at");
        b.Property(x => x.MustChangePassword).HasColumnName("must_change_password").HasDefaultValue(false);
        b.Property(x => x.FailedAttempts).HasColumnName("failed_attempts").HasDefaultValue(0);
        b.Property(x => x.LockedUntil).HasColumnName("locked_until");
        b.Property(x => x.Disabled).HasColumnName("disabled").HasDefaultValue(false);
        b.Property(x => x.TotpSecretEnc).HasColumnName("totp_secret_enc");
        b.Property(x => x.RoleNames).HasColumnName("roles").HasColumnType("text[]");
        b.Property(x => x.Scopes).HasColumnName("scopes").HasColumnType("text[]");
        b.Property(x => x.LastLoginAt).HasColumnName("last_login_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Ignore(x => x.IsPlatformAdmin);
        b.HasIndex(x => x.Username, "user_username_uq").IsUnique();
        b.HasIndex(x => x.Email, "user_email_uq").IsUnique().HasFilter("email IS NOT NULL");
    }
}

internal sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> b)
    {
        b.ToTable("session", "cfg");
        b.HasKey(x => x.SessionId);
        b.Property(x => x.SessionId).HasColumnName("session_id").HasMaxLength(64);
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.LastSeenAt).HasColumnName("last_seen_at");
        b.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        b.Property(x => x.AbsoluteExpiresAt).HasColumnName("absolute_expires_at");
        b.Property(x => x.Ip).HasColumnName("ip").HasColumnType("inet");
        b.Property(x => x.UserAgent).HasColumnName("user_agent");
        b.Property(x => x.RevokedAt).HasColumnName("revoked_at");
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).HasConstraintName("session_user_fk").OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.UserId, x.ExpiresAt }, "session_user_idx");
        b.HasIndex(x => x.ExpiresAt, "session_expires_idx");
    }
}

internal sealed class PersonalAccessTokenConfiguration : IEntityTypeConfiguration<PersonalAccessToken>
{
    public void Configure(EntityTypeBuilder<PersonalAccessToken> b)
    {
        b.ToTable("personal_access_token", "cfg");
        b.HasKey(x => x.TokenId);
        b.Property(x => x.TokenId).HasColumnName("token_id");
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.Name).HasColumnName("name");
        b.Property(x => x.KeyId).HasColumnName("key_id").HasMaxLength(12);
        b.Property(x => x.TokenHash).HasColumnName("token_hash");
        b.Property(x => x.Scopes).HasColumnName("scopes").HasColumnType("text[]");
        b.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        b.Property(x => x.LastUsedAt).HasColumnName("last_used_at");
        b.Property(x => x.RevokedAt).HasColumnName("revoked_at");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).HasConstraintName("pat_user_fk").OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.KeyId, "pat_key_uq").IsUnique();
        b.HasIndex(x => x.UserId, "pat_user_idx");
    }
}

internal sealed class LoginAttemptConfiguration : IEntityTypeConfiguration<LoginAttempt>
{
    public void Configure(EntityTypeBuilder<LoginAttempt> b)
    {
        b.ToTable("login_attempt", "cfg");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.At).HasColumnName("at");
        b.Property(x => x.Username).HasColumnName("username");
        b.Property(x => x.Ip).HasColumnName("ip").HasColumnType("inet");
        b.Property(x => x.Success).HasColumnName("success");
        b.HasIndex(x => new { x.Username, x.At }, "login_attempt_user_idx");
        b.HasIndex(x => new { x.Ip, x.At }, "login_attempt_ip_idx");
    }
}

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> b)
    {
        b.ToTable("idempotency_key", "cfg");
        b.HasKey(x => new { x.UserId, x.Key });
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.Key).HasColumnName("key").HasMaxLength(128);
        b.Property(x => x.RequestFingerprint).HasColumnName("request_fingerprint");
        b.Property(x => x.StatusCode).HasColumnName("status_code");
        b.Property(x => x.ResponseBody).HasColumnName("response_body");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        b.HasIndex(x => x.ExpiresAt, "idempotency_expires_idx");
    }
}

internal sealed class TeamMemberConfiguration : IEntityTypeConfiguration<TeamMember>
{
    public void Configure(EntityTypeBuilder<TeamMember> b)
    {
        b.ToTable("team_member", "cfg");
        b.HasKey(x => new { x.TeamId, x.UserId });
        b.Property(x => x.TeamId).HasColumnName("team_id");
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.Role).HasColumnName("role").HasDefaultValue("member");
        b.HasOne<Domain.Teams.Team>().WithMany().HasForeignKey(x => x.TeamId).HasConstraintName("team_member_team_fk").OnDelete(DeleteBehavior.Cascade);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).HasConstraintName("team_member_user_fk").OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.UserId, "team_member_user_idx");
    }
}

internal sealed class SavedFilterConfiguration : IEntityTypeConfiguration<SavedFilter>
{
    public void Configure(EntityTypeBuilder<SavedFilter> b)
    {
        b.ToTable("saved_filter", "cfg");
        b.HasKey(x => x.FilterId);
        b.Property(x => x.FilterId).HasColumnName("filter_id");
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.Name).HasColumnName("name");
        b.Property(x => x.Query).HasColumnName("query");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).HasConstraintName("saved_filter_user_fk").OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.UserId, x.Name }, "saved_filter_user_name_uq").IsUnique();
    }
}
