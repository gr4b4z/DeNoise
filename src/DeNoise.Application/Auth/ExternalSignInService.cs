using System.Text.Json;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Audit;
using DeNoise.Domain.Audit;
using DeNoise.Domain.Common;
using DeNoise.Domain.Users;
using Microsoft.Extensions.Options;

namespace DeNoise.Application.Auth;

/// <summary>Outcome of an SSO sign-in: a session token for the cookie, or the refusal reason that was audited.</summary>
public sealed record ExternalSignInResult(string? SessionToken, User? User, DateTimeOffset? ExpiresAt, string? RefusalReason)
{
    public bool Succeeded => SessionToken is not null;
}

/// <summary>
/// The second half of OIDC sign-in (M11b, ADR-15), in one transaction: match the verified external identity to a local
/// user, project roles and scopes onto it, provision it on first sign-in, and issue the same server-side session a
/// password login issues. Authorization keeps reading the local row, so nothing downstream changes.
/// <list type="bullet">
/// <item>Match on <c>(external_issuer, external_id)</c>; else on e-mail for a pre-SSO <c>local</c> account that is not
/// <c>local_only</c>, not linked yet and not a guest token. Several case variants of the e-mail, or an e-mail held by an
/// account that may not be linked, refuse the sign-in rather than guess.</item>
/// <item>A token with no mapped app role is refused and audited and creates no row. For an existing projected user it
/// also lowers the stored roles to none, so PATs that user issued narrow at once.</item>
/// <item><c>local_only</c> accounts are never projected.</item>
/// </list>
/// </summary>
public sealed class ExternalSignInService(
    IUserRepository users, SessionIssuer sessions, IAuditWriter audit, IUnitOfWork uow, TimeProvider time, IOptions<OidcAuthOptions> options)
{
    public const string AuditRefused = "auth.oidc_refused";
    public const string AuditProvisioned = "user.provisioned";
    public const string AuditRolesProjected = "user.roles_projected";

    public async Task<ExternalSignInResult> SignInAsync(ExternalPrincipal principal, System.Net.IPAddress? ip, string? userAgent, string correlationId, CancellationToken ct = default)
    {
        ExternalSignInResult result = null!;
        await uow.InTransactionAsync(async () => result = await SignInCoreAsync(principal, ip, userAgent, correlationId, ct), ct);
        return result;
    }

    /// <summary>Audits a refusal that happened before a principal existed (state, code, token validation). Commits.</summary>
    public async Task RecordRefusalAsync(string reason, string? subject, string? detail, System.Net.IPAddress? ip, string correlationId, CancellationToken ct = default)
    {
        Refuse(reason, subject, null, correlationId, ip, detail is null ? null : new { detail });
        await uow.CommitAsync(ct);
    }

    private async Task<ExternalSignInResult> SignInCoreAsync(ExternalPrincipal p, System.Net.IPAddress? ip, string? userAgent, string correlationId, CancellationToken ct)
    {
        var o = options.Value;
        var access = ExternalAccessProjection.Project(p, o);
        var now = time.GetUtcNow();

        var user = await users.FindByExternalIdentityAsync(p.Issuer, p.Subject, ct);
        var matchedBy = user is null ? null : "external_id";
        var email = string.IsNullOrWhiteSpace(p.Email) ? null : p.Email.Trim();
        if (user is null && email is not null)
        {
            var holders = await users.ListByEmailIgnoreCaseAsync(email, ct);
            if (holders.Count > 0)
            {
                var candidate = holders.Count == 1 ? holders[0] : null;
                var linkable = o.MatchByEmail && !p.IsGuest && candidate is { LocalOnly: false, ExternalId: null, AuthProvider: AuthProviders.Local };
                if (!linkable)
                {
                    // Never guess: the e-mail belongs to an account this identity may not take over (local_only, already linked,
                    // a guest token, matching off, or several case variants). An administrator links or renames it.
                    return await RefuseAsync(OidcRefusalReasons.EmailConflict, p, correlationId, ip, new { holders = holders.Count, guest = p.IsGuest }, ct);
                }
                user = candidate;
                matchedBy = "email";
            }
        }

        if (user is { Disabled: true }) return await RefuseAsync(OidcRefusalReasons.UserDisabled, p, correlationId, ip, new { userId = user.UserId }, ct);

        if (access.Roles.Length == 0)
        {
            if (user is not null && matchedBy == "external_id" && !user.LocalOnly && (user.RoleNames.Length > 0 || user.Scopes.Length > 0))
            {
                // The token proves the user no longer holds a mapped role: lower the stored projection too (least privilege for PATs).
                var before = new { roles = user.RoleNames, scopes = user.Scopes };
                user.RoleNames = [];
                user.Scopes = [];
                user.RolesProjectedAt = now;
                Touch(user, now);
                Record(AuditRolesProjected, user, correlationId, ip, new { roles = user.RoleNames, scopes = user.Scopes, unmappedRoles = access.UnmappedRoles }, before);
            }
            return await RefuseAsync(OidcRefusalReasons.NoMappedRole, p, correlationId, ip, new { unmappedRoles = access.UnmappedRoles }, ct);
        }

        if (user is null)
        {
            if (!o.AutoProvision) return await RefuseAsync(OidcRefusalReasons.NotProvisioned, p, correlationId, ip, null, ct);
            user = new User
            {
                UserId = Ids.New(time),
                Username = await UniqueUsernameAsync(p, ct),
                DisplayName = DisplayNameOf(p),
                Email = email,
                AuthProvider = AuthProviders.Oidc,
                ExternalIssuer = p.Issuer,
                ExternalId = p.Subject,
                RoleNames = access.Roles,
                Scopes = access.Scopes,
                RolesProjectedAt = now,
                CreatedBy = "oidc",
                CreatedAt = now,
                UpdatedAt = now,
            };
            users.Add(user);
            Record(AuditProvisioned, user, correlationId, ip, new
            {
                user.Username,
                issuer = p.Issuer,
                externalId = p.Subject,
                roles = user.RoleNames,
                scopes = user.Scopes,
                unmappedRoles = access.UnmappedRoles,
                groupsOverage = access.GroupsOverage,
            });
        }
        else if (!user.LocalOnly)
        {
            var before = new { roles = user.RoleNames, scopes = user.Scopes, authProvider = user.AuthProvider };
            var linking = matchedBy == "email";
            var changed = linking || !user.RoleNames.SequenceEqual(access.Roles, StringComparer.Ordinal) || !user.Scopes.SequenceEqual(access.Scopes, StringComparer.Ordinal);
            if (linking)
            {
                // ADR-14 migration note: the pre-SSO account keeps its id, history, teams and PATs; its password is retired.
                user.AuthProvider = AuthProviders.Oidc;
                user.ExternalIssuer = p.Issuer;
                user.ExternalId = p.Subject;
                user.PasswordHash = null;
                user.MustChangePassword = false;
                user.FailedAttempts = 0;
                user.LockedUntil = null;
            }
            user.RoleNames = access.Roles;
            user.Scopes = access.Scopes;
            user.RolesProjectedAt = now;
            if (p.DisplayName is { Length: > 0 } name && name != user.DisplayName) user.DisplayName = name.Trim();
            Touch(user, now);
            if (changed)
            {
                Record(AuditRolesProjected, user, correlationId, ip, new
                {
                    roles = user.RoleNames,
                    scopes = user.Scopes,
                    authProvider = user.AuthProvider,
                    matchedBy,
                    unmappedRoles = access.UnmappedRoles,
                    groupsOverage = access.GroupsOverage,
                }, before);
            }
        }

        user.LastLoginAt = now;
        user.UpdatedAt = now;
        var (token, session) = sessions.Issue(user, ip, userAgent);
        Record("auth.login", user, correlationId, ip, new { provider = AuthProviders.Oidc, matchedBy = matchedBy ?? "provisioned", localOnly = user.LocalOnly });
        await uow.CommitAsync(ct);
        return new ExternalSignInResult(token, user, session.ExpiresAt, null);
    }

    private async Task<ExternalSignInResult> RefuseAsync(string reason, ExternalPrincipal p, string correlationId, System.Net.IPAddress? ip, object? detail, CancellationToken ct)
    {
        Refuse(reason, p.Subject, p.Issuer, correlationId, ip, detail);
        await uow.CommitAsync(ct);
        return new ExternalSignInResult(null, null, null, reason);
    }

    private void Refuse(string reason, string? subject, string? issuer, string correlationId, System.Net.IPAddress? ip, object? detail)
        => audit.Record(new AuditEntry
        {
            Id = Ids.New(time),
            At = time.GetUtcNow(),
            ActorType = ActorTypes.System,
            ActorId = "anonymous",
            ActorDisplay = null,
            Action = AuditRefused,
            TargetType = "user",
            TargetId = subject ?? "?",
            After = JsonSerializer.Serialize(new { reason, provider = AuthProviders.Oidc, issuer, externalId = subject, detail }, JsonDefaults.Stored),
            CorrelationId = correlationId,
            RequestIp = ip,
        });

    private void Record(string action, User user, string correlationId, System.Net.IPAddress? ip, object? after, object? before = null)
        => audit.Record(new AuditEntry
        {
            Id = Ids.New(time),
            At = time.GetUtcNow(),
            ActorType = ActorTypes.User,
            ActorId = user.UserId.ToString(),
            ActorDisplay = user.Username,
            Action = action,
            TargetType = "user",
            TargetId = user.UserId.ToString(),
            Before = before is null ? null : JsonSerializer.Serialize(before, JsonDefaults.Stored),
            After = after is null ? null : JsonSerializer.Serialize(after, JsonDefaults.Stored),
            CorrelationId = correlationId,
            RequestIp = ip,
        });

    private static void Touch(User user, DateTimeOffset now)
    {
        user.UpdatedAt = now;
        user.Version++;
    }

    private static string DisplayNameOf(ExternalPrincipal p)
        => new[] { p.DisplayName, p.PreferredUsername, p.Email }.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim() ?? $"SSO user {p.Subject[..Math.Min(8, p.Subject.Length)]}";

    /// <summary>preferred_username, else e-mail, else <c>oidc-&lt;oid&gt;</c>; suffixed with the oid on collision. Same alphabet as admin-created usernames.</summary>
    private async Task<string> UniqueUsernameAsync(ExternalPrincipal p, CancellationToken ct)
    {
        var oid = new string(p.Subject.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var fallback = $"oidc-{oid}";
        if (fallback.Length > 64) fallback = fallback[..64];
        var candidate = Sanitize(p.PreferredUsername) ?? Sanitize(p.Email) ?? fallback;
        if (await users.FindByUsernameAsync(candidate, ct) is null) return candidate;
        var suffix = "-" + oid[..Math.Min(8, oid.Length)];
        var suffixed = (candidate.Length + suffix.Length > 64 ? candidate[..(64 - suffix.Length)] : candidate) + suffix;
        return await users.FindByUsernameAsync(suffixed, ct) is null ? suffixed : fallback;
    }

    private static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim().ToLowerInvariant();
        return s.Length is >= 2 and <= 64 && s.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '@') ? s : null;
    }
}
