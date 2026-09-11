using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Audit;
using AlertHub.Application.Integrations;
using AlertHub.Domain.Audit;
using AlertHub.Domain.Common;
using AlertHub.Domain.Users;
using Microsoft.Extensions.Options;

namespace AlertHub.Application.Auth;

public enum LoginOutcome
{
    Success,
    InvalidCredentials,
    Locked,
}

/// <summary>Result of a login: the plaintext session token goes into the cookie and nowhere else.</summary>
public sealed record LoginResult(LoginOutcome Outcome, string? SessionToken = null, User? User = null, DateTimeOffset? LockedUntil = null, DateTimeOffset? ExpiresAt = null);

/// <summary>
/// Login / logout / password change over the configured <see cref="IIdentityProvider"/> with server-side sessions
/// (ADR-14). Session tokens are 32 random bytes; the database stores only their SHA-256, so a dumped table
/// cannot be replayed. Every auth event is audited (06 §4 Auth).
/// </summary>
public sealed class AuthService(
    IEnumerable<IIdentityProvider> providers, IUserRepository users, ISessionRepository sessions, ISecretHasher hasher,
    IAuditWriter audit, IUnitOfWork uow, TimeProvider time, IOptions<LocalAuthOptions> options)
{
    private readonly IIdentityProvider _local = providers.First(p => p.Name == AuthProviders.Local);

    public async Task<LoginResult> LoginAsync(string username, string password, System.Net.IPAddress? ip, string? userAgent, string correlationId, CancellationToken ct = default)
    {
        var result = await _local.AuthenticateAsync(username, password, ip, ct);
        var now = time.GetUtcNow();
        switch (result.Status)
        {
            case AuthenticationStatus.Locked:
                Audit("auth.locked", username, null, correlationId, ip, new { lockedUntil = result.LockedUntil });
                await uow.CommitAsync(ct);
                return new LoginResult(LoginOutcome.Locked, LockedUntil: result.LockedUntil);
            case AuthenticationStatus.InvalidCredentials:
                Audit("auth.login_failed", username, null, correlationId, ip, null);
                await uow.CommitAsync(ct);
                return new LoginResult(LoginOutcome.InvalidCredentials);
        }

        var user = result.User!;
        var token = TokenGenerator.NewSecret();
        var session = new Session
        {
            SessionId = HashToken(token),
            UserId = user.UserId,
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now + options.Value.SessionSliding,
            AbsoluteExpiresAt = now + options.Value.SessionAbsolute,
            Ip = ip,
            UserAgent = Truncate(userAgent, 300),
        };
        sessions.Add(session);
        Audit("auth.login", user.Username, user.UserId, correlationId, ip, new { mustChangePassword = user.MustChangePassword });
        await uow.CommitAsync(ct);
        return new LoginResult(LoginOutcome.Success, token, user, ExpiresAt: session.ExpiresAt);
    }

    /// <summary>Resolves a cookie token to its user, sliding the expiry (at most once a minute to keep writes low).</summary>
    public async Task<(User User, Session Session)?> ResolveSessionAsync(string token, CancellationToken ct = default)
    {
        var session = await sessions.GetAsync(HashToken(token), ct);
        var now = time.GetUtcNow();
        if (session is null || !session.IsActive(now)) return null;
        var user = await users.GetAsync(session.UserId, ct);
        if (user is null || user.Disabled) return null;

        if (now - session.LastSeenAt > TimeSpan.FromMinutes(1))
        {
            session.LastSeenAt = now;
            var slid = now + options.Value.SessionSliding;
            session.ExpiresAt = slid < session.AbsoluteExpiresAt ? slid : session.AbsoluteExpiresAt;
            await uow.CommitAsync(ct);
        }
        return (user, session);
    }

    public async Task LogoutAsync(string token, Guid userId, string correlationId, System.Net.IPAddress? ip, CancellationToken ct = default)
    {
        var session = await sessions.GetAsync(HashToken(token), ct);
        if (session is null || session.RevokedAt is not null) return;
        session.RevokedAt = time.GetUtcNow();
        Audit("auth.logout", null, userId, correlationId, ip, null);
        await uow.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<string>> ChangePasswordAsync(AlertHubPrincipal principal, string current, string next, string correlationId, System.Net.IPAddress? ip, CancellationToken ct = default)
    {
        var user = await users.GetAsync(principal.UserId, ct) ?? throw new KeyNotFoundException("user not found");
        if (user.PasswordHash is null || !hasher.Verify(current, user.PasswordHash))
        {
            return ["current password is incorrect"];
        }
        var problems = PasswordPolicy.Validate(next, options.Value.PasswordMinLength);
        if (problems.Count > 0) return problems;
        if (hasher.Verify(next, user.PasswordHash)) return ["new password must differ from the current one"];

        var now = time.GetUtcNow();
        user.PasswordHash = hasher.HashPassword(next);
        user.PasswordChangedAt = now;
        user.MustChangePassword = false;
        user.UpdatedAt = now;
        user.Version++;
        // Every other session dies with the old password; the current one stays valid.
        await sessions.RevokeAllAsync(user.UserId, principal.SessionId, now, ct);
        Audit("auth.password_changed", user.Username, user.UserId, correlationId, ip, null);
        await uow.CommitAsync(ct);
        return [];
    }

    public async Task<IReadOnlyList<Session>> ListSessionsAsync(Guid userId, CancellationToken ct = default)
        => await sessions.ListActiveForUserAsync(userId, time.GetUtcNow(), ct);

    public async Task<bool> RevokeSessionAsync(Guid userId, string sessionId, string correlationId, CancellationToken ct = default)
    {
        var session = await sessions.GetAsync(sessionId, ct);
        if (session is null || session.UserId != userId || session.RevokedAt is not null) return false;
        session.RevokedAt = time.GetUtcNow();
        Audit("auth.session_revoked", null, userId, correlationId, null, new { sessionId = sessionId[..8] });
        await uow.CommitAsync(ct);
        return true;
    }

    /// <summary>First start with no users: seed <c>admin</c> with the bootstrap password (or a generated one, returned so the host can print it once).</summary>
    public async Task<string?> BootstrapAdminIfEmptyAsync(CancellationToken ct = default)
    {
        if (await users.CountAsync(ct) > 0) return null;
        var password = options.Value.BootstrapPassword;
        var generated = string.IsNullOrWhiteSpace(password);
        if (generated) password = PasswordPolicy.GenerateTemporary() + PasswordPolicy.GenerateTemporary()[..8];
        var now = time.GetUtcNow();
        var admin = new User
        {
            UserId = Ids.New(time),
            Username = "admin",
            DisplayName = "Administrator",
            PasswordHash = hasher.HashPassword(password!),
            PasswordChangedAt = now,
            MustChangePassword = true,
            RoleNames = [Roles.PlatformAdmin],
            Scopes = [],
            CreatedBy = "bootstrap",
            CreatedAt = now,
            UpdatedAt = now,
        };
        users.Add(admin);
        Audit("user.bootstrap", admin.Username, admin.UserId, "bootstrap", null, new { generatedPassword = generated });
        await uow.CommitAsync(ct);
        return generated ? password : string.Empty;
    }

    public static string HashToken(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private void Audit(string action, string? username, Guid? userId, string correlationId, System.Net.IPAddress? ip, object? after)
        => audit.Record(new AuditEntry
        {
            Id = Ids.New(time),
            At = time.GetUtcNow(),
            ActorType = userId is null ? ActorTypes.System : ActorTypes.User,
            ActorId = userId?.ToString() ?? "anonymous",
            ActorDisplay = username,
            Action = action,
            TargetType = "user",
            TargetId = userId?.ToString() ?? username ?? "?",
            After = after is null ? null : JsonSerializer.Serialize(after, JsonDefaults.Stored),
            CorrelationId = correlationId,
            RequestIp = ip,
        });

    private static string? Truncate(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
