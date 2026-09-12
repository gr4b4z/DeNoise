namespace DeNoise.Domain.Users;

public static class AuthProviders
{
    public const string Local = "local";
    public const string Oidc = "oidc";
}

/// <summary>
/// Hub user (<c>cfg.user</c>, 05 §6, ADR-14). Authorization (roles, scopes, team membership) is always local
/// regardless of how the user proves identity.
/// </summary>
public sealed class User
{
    public Guid UserId { get; init; }
    public required string Username { get; init; }
    public string? Email { get; set; }
    public required string DisplayName { get; set; }
    public string AuthProvider { get; init; } = AuthProviders.Local;
    public string? ExternalId { get; set; }
    public string? PasswordHash { get; set; }
    public DateTimeOffset? PasswordChangedAt { get; set; }
    public bool MustChangePassword { get; set; }
    public int FailedAttempts { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public bool Disabled { get; set; }
    public string? TotpSecretEnc { get; set; }
    /// <summary>See <see cref="Roles"/>.</summary>
    public string[] RoleNames { get; set; } = [];
    /// <summary>Access scopes the user may see; ignored for <c>platform_admin</c> (all scopes).</summary>
    public string[] Scopes { get; set; } = [];
    public DateTimeOffset? LastLoginAt { get; set; }
    public string? CreatedBy { get; init; }
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsLocked(DateTimeOffset now) => LockedUntil is { } until && until > now;
    public bool IsPlatformAdmin => RoleNames.Contains(Roles.PlatformAdmin, StringComparer.Ordinal);
    public bool CanSeeScope(string scope) => IsPlatformAdmin || Scopes.Contains(scope, StringComparer.Ordinal);
}

/// <summary>Server-side session (<c>cfg.session</c>). The cookie carries only a random token; the row stores its SHA-256.</summary>
public sealed class Session
{
    public required string SessionId { get; init; }
    public Guid UserId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    /// <summary>Absolute lifetime cap (24 h); sliding <see cref="ExpiresAt"/> never passes it.</summary>
    public DateTimeOffset AbsoluteExpiresAt { get; init; }
    public System.Net.IPAddress? Ip { get; init; }
    public string? UserAgent { get; init; }
    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now && AbsoluteExpiresAt > now;
}

/// <summary>Personal access token (<c>cfg.personal_access_token</c>): <c>ah_pat_&lt;keyId&gt;.&lt;secret&gt;</c>, hashed, scoped ⊆ owner (ADR-14).</summary>
public sealed class PersonalAccessToken
{
    public Guid TokenId { get; init; }
    public Guid UserId { get; init; }
    public required string Name { get; init; }
    public required string KeyId { get; init; }
    public required string TokenHash { get; init; }
    /// <summary>Permission names the token may exercise; empty = all of the owner's permissions.</summary>
    public string[] Scopes { get; init; } = [];
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; init; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);
}

/// <summary>Login attempt for lockout and audit (<c>cfg.login_attempt</c>, 30 d retention).</summary>
public sealed class LoginAttempt
{
    public Guid Id { get; init; }
    public DateTimeOffset At { get; init; }
    public required string Username { get; init; }
    public System.Net.IPAddress? Ip { get; init; }
    public bool Success { get; init; }
}

/// <summary>Stored response for an <c>Idempotency-Key</c> (06 §1): 24 h per user; replay returns the original response.</summary>
public sealed class IdempotencyRecord
{
    public Guid UserId { get; init; }
    public required string Key { get; init; }
    public required string RequestFingerprint { get; init; }
    public int StatusCode { get; init; }
    public string? ResponseBody { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>Team membership (<c>cfg.team_member</c>).</summary>
public sealed class TeamMember
{
    public Guid TeamId { get; init; }
    public Guid UserId { get; init; }
    public string Role { get; set; } = "member";
}

/// <summary>Saved queue filter (<c>cfg.saved_filter</c>, 06 §4 <c>/me/filters</c>).</summary>
public sealed class SavedFilter
{
    public Guid FilterId { get; init; }
    public Guid UserId { get; init; }
    public required string Name { get; set; }
    public required string Query { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}
