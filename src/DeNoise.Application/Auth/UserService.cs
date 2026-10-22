using System.Text.Json;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Audit;
using DeNoise.Domain.Audit;
using DeNoise.Domain.Common;
using DeNoise.Domain.Users;

namespace DeNoise.Application.Auth;

public sealed record CreateUser(string Username, string DisplayName, string? Email, string[] Roles, string[] Scopes);
public sealed record UpdateUser(string? DisplayName, string? Email, string[]? Roles, string[]? Scopes);

/// <summary>Admin user management (<c>user.manage</c>): create with a one-time temporary password, roles/scopes, reset, disable/enable, unlock.</summary>
public sealed class UserService(IUserRepository users, ISessionRepository sessions, ISecretHasher hasher, IAuditWriter audit, IUnitOfWork uow, TimeProvider time)
{
    public async Task<(User User, string TemporaryPassword)> CreateAsync(CreateUser request, DeNoisePrincipal actor, string correlationId, CancellationToken ct = default)
    {
        Require(actor, Permissions.UserManage);
        var username = request.Username.Trim().ToLowerInvariant();
        if (username.Length is < 2 or > 64 || username.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '@')))
        {
            throw new ArgumentException("username must be 2–64 characters of letters, digits, '.', '-', '_' or '@'", nameof(request));
        }
        if (await users.FindByUsernameAsync(username, ct) is not null) throw new InvalidOperationException($"username '{username}' is taken");
        if (!string.IsNullOrWhiteSpace(request.Email) && await users.FindByEmailAsync(request.Email.Trim(), ct) is not null) throw new InvalidOperationException("email is already in use");
        ValidateRoles(request.Roles);

        var now = time.GetUtcNow();
        var temporary = PasswordPolicy.GenerateTemporary();
        var user = new User
        {
            UserId = Ids.New(time),
            Username = username,
            DisplayName = request.DisplayName.Trim(),
            Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
            PasswordHash = hasher.HashPassword(temporary),
            PasswordChangedAt = now,
            MustChangePassword = true,
            RoleNames = request.Roles,
            Scopes = request.Scopes,
            CreatedBy = actor.UserId.ToString(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        users.Add(user);
        Audit(actor, "user.create", user, correlationId, new { user.Username, user.RoleNames, user.Scopes });
        await uow.CommitAsync(ct);
        return (user, temporary);
    }

    public async Task<User> UpdateAsync(Guid userId, UpdateUser request, DeNoisePrincipal actor, string correlationId, CancellationToken ct = default)
    {
        Require(actor, Permissions.UserManage);
        var user = await users.GetAsync(userId, ct) ?? throw new KeyNotFoundException("user not found");
        var before = new { user.DisplayName, user.Email, user.RoleNames, user.Scopes };
        if (request.Roles is not null)
        {
            ValidateRoles(request.Roles);
            if (user.UserId == actor.UserId && !request.Roles.Contains(Roles.PlatformAdmin)) throw new InvalidOperationException("you cannot remove your own platform_admin role");
            user.RoleNames = request.Roles;
        }
        if (request.Scopes is not null) user.Scopes = request.Scopes;
        if (request.DisplayName is not null) user.DisplayName = request.DisplayName.Trim();
        if (request.Email is not null) user.Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
        Touch(user);
        Audit(actor, "user.update", user, correlationId, new { user.DisplayName, user.Email, user.RoleNames, user.Scopes }, before);
        await uow.CommitAsync(ct);
        return user;
    }

    public async Task<string> ResetPasswordAsync(Guid userId, DeNoisePrincipal actor, string correlationId, CancellationToken ct = default)
    {
        Require(actor, Permissions.UserManage);
        var user = await users.GetAsync(userId, ct) ?? throw new KeyNotFoundException("user not found");
        var temporary = PasswordPolicy.GenerateTemporary();
        user.PasswordHash = hasher.HashPassword(temporary);
        user.PasswordChangedAt = time.GetUtcNow();
        user.MustChangePassword = true;
        user.FailedAttempts = 0;
        user.LockedUntil = null;
        Touch(user);
        await sessions.RevokeAllAsync(user.UserId, null, time.GetUtcNow(), ct);
        Audit(actor, "user.reset_password", user, correlationId, null);
        await uow.CommitAsync(ct);
        return temporary;
    }

    public async Task SetDisabledAsync(Guid userId, bool disabled, DeNoisePrincipal actor, string correlationId, CancellationToken ct = default)
    {
        Require(actor, Permissions.UserManage);
        var user = await users.GetAsync(userId, ct) ?? throw new KeyNotFoundException("user not found");
        if (disabled && user.UserId == actor.UserId) throw new InvalidOperationException("you cannot disable yourself");
        user.Disabled = disabled;
        Touch(user);
        if (disabled) await sessions.RevokeAllAsync(user.UserId, null, time.GetUtcNow(), ct);
        Audit(actor, disabled ? "user.disable" : "user.enable", user, correlationId, new { disabled });
        await uow.CommitAsync(ct);
    }

    public async Task UnlockAsync(Guid userId, DeNoisePrincipal actor, string correlationId, CancellationToken ct = default)
    {
        Require(actor, Permissions.UserManage);
        var user = await users.GetAsync(userId, ct) ?? throw new KeyNotFoundException("user not found");
        user.LockedUntil = null;
        user.FailedAttempts = 0;
        Touch(user);
        Audit(actor, "user.unlock", user, correlationId, null);
        await uow.CommitAsync(ct);
    }

    public Task<IReadOnlyList<User>> ListAsync(string? query, int limit, CancellationToken ct = default) => users.ListAsync(query, Math.Clamp(limit, 1, 200), ct);
    public Task<User?> GetAsync(Guid userId, CancellationToken ct = default) => users.GetAsync(userId, ct);

    private void Touch(User user)
    {
        user.UpdatedAt = time.GetUtcNow();
        user.Version++;
    }

    private static void Require(DeNoisePrincipal actor, string permission)
    {
        if (!actor.Has(permission)) throw new ForbiddenException(permission);
    }

    private static void ValidateRoles(string[] roles)
    {
        var unknown = roles.Where(r => !Roles.All.Contains(r)).ToList();
        if (unknown.Count > 0) throw new ArgumentException($"unknown roles: {string.Join(", ", unknown)}");
    }

    private void Audit(DeNoisePrincipal actor, string action, User target, string correlationId, object? after, object? before = null)
        => audit.Record(new AuditEntry
        {
            Id = Ids.New(time),
            At = time.GetUtcNow(),
            ActorType = ActorTypes.User,
            ActorId = actor.UserId.ToString(),
            ActorDisplay = actor.Username,
            Action = action,
            TargetType = "user",
            TargetId = target.UserId.ToString(),
            Before = before is null ? null : JsonSerializer.Serialize(before, JsonDefaults.Stored),
            After = after is null ? null : JsonSerializer.Serialize(after, JsonDefaults.Stored),
            CorrelationId = correlationId,
        });
}

/// <summary>Personal access tokens: <c>ah_pat_&lt;keyId&gt;.&lt;secret&gt;</c>, shown once, scoped ⊆ owner, hashed at rest (ADR-14, 06 §4).</summary>
public sealed class PersonalAccessTokenService(IPersonalAccessTokenRepository tokens, IUserRepository users, ISecretHasher hasher, IAuditWriter audit, IUnitOfWork uow, TimeProvider time)
{
    public const string Prefix = "ah_pat_";

    public async Task<(PersonalAccessToken Token, string Plaintext)> CreateAsync(DeNoisePrincipal actor, string name, string[] scopes, DateTimeOffset? expiresAt, string correlationId, CancellationToken ct = default)
    {
        if (!actor.Has(Permissions.MeTokens)) throw new ForbiddenException(Permissions.MeTokens);
        if (actor.Credential == CredentialKind.PersonalAccessToken) throw new ForbiddenException(Permissions.MeTokens, "a token cannot mint tokens");
        var outside = scopes.Where(s => !actor.Permissions.Contains(s)).ToList();
        if (outside.Count > 0) throw new ForbiddenException(Permissions.MeTokens, $"token scopes exceed your permissions: {string.Join(", ", outside)}");
        var now = time.GetUtcNow();
        if (expiresAt is { } exp && exp <= now) throw new ArgumentException("expiry must be in the future", nameof(expiresAt));

        var keyId = Integrations.TokenGenerator.NewKeyId();
        var secret = Integrations.TokenGenerator.NewSecret();
        var token = new PersonalAccessToken
        {
            TokenId = Ids.New(time),
            UserId = actor.UserId,
            Name = name.Trim(),
            KeyId = keyId,
            TokenHash = hasher.HashToken(secret),
            Scopes = scopes,
            ExpiresAt = expiresAt,
            CreatedAt = now,
        };
        tokens.Add(token);
        audit.Record(new AuditEntry
        {
            Id = Ids.New(time),
            At = now,
            ActorType = ActorTypes.User,
            ActorId = actor.UserId.ToString(),
            ActorDisplay = actor.Username,
            Action = "me.token_create",
            TargetType = "token",
            TargetId = token.TokenId.ToString(),
            After = JsonSerializer.Serialize(new { name = token.Name, keyId, scopes, expiresAt }, JsonDefaults.Stored),
            CorrelationId = correlationId,
        });
        await uow.CommitAsync(ct);
        return (token, $"{Prefix}{keyId}.{secret}");
    }

    public Task<IReadOnlyList<PersonalAccessToken>> ListAsync(Guid userId, CancellationToken ct = default) => tokens.ListForUserAsync(userId, ct);

    public async Task<bool> RevokeAsync(DeNoisePrincipal actor, Guid tokenId, string correlationId, CancellationToken ct = default)
    {
        var token = await tokens.GetAsync(tokenId, ct);
        if (token is null || token.UserId != actor.UserId || token.RevokedAt is not null) return false;
        token.RevokedAt = time.GetUtcNow();
        audit.Record(new AuditEntry
        {
            Id = Ids.New(time),
            At = time.GetUtcNow(),
            ActorType = ActorTypes.User,
            ActorId = actor.UserId.ToString(),
            ActorDisplay = actor.Username,
            Action = "me.token_revoke",
            TargetType = "token",
            TargetId = tokenId.ToString(),
            CorrelationId = correlationId,
        });
        await uow.CommitAsync(ct);
        return true;
    }

    /// <summary>Bearer → principal, or null for anything that does not verify. Expired and revoked tokens are null; the owner must be enabled.</summary>
    public async Task<DeNoisePrincipal?> AuthenticateAsync(string bearer, CancellationToken ct = default)
    {
        if (!bearer.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var dot = bearer.IndexOf('.', Prefix.Length);
        if (dot < 0) return null;
        var keyId = bearer[Prefix.Length..dot];
        var secret = bearer[(dot + 1)..];
        if (keyId.Length != Integrations.TokenGenerator.KeyIdLength || secret.Length == 0) return null;

        var token = await tokens.FindByKeyIdAsync(keyId, ct);
        var now = time.GetUtcNow();
        if (token is null || !token.IsActive(now) || !hasher.Verify(secret, token.TokenHash)) return null;
        var user = await users.GetAsync(token.UserId, ct);
        if (user is null || user.Disabled) return null;
        if (token.LastUsedAt is null || now - token.LastUsedAt > TimeSpan.FromMinutes(1))
        {
            await tokens.TouchLastUsedAsync(token.TokenId, now, ct);
        }
        return DeNoisePrincipal.ForToken(user, token);
    }
}
