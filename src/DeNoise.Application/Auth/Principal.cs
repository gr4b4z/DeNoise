using DeNoise.Domain.Users;

namespace DeNoise.Application.Auth;

public enum CredentialKind
{
    Session,
    PersonalAccessToken,
}

/// <summary>
/// The authenticated caller as seen by handlers: identity plus the effective permission set. For a personal
/// access token the permissions are the intersection of the token's scopes and the owner's roles (ADR-14).
/// </summary>
public sealed record DeNoisePrincipal(
    Guid UserId, string Username, string DisplayName, IReadOnlyList<string> Roles, IReadOnlyList<string> Scopes,
    IReadOnlySet<string> Permissions, bool MustChangePassword, CredentialKind Credential, string? SessionId = null, Guid? TokenId = null)
{
    public bool IsPlatformAdmin => Roles.Contains(Domain.Users.Roles.PlatformAdmin);

    public bool Has(string permission) => Permissions.Contains(permission);

    /// <summary>Scope enforcement in every query (06 §1): platform admins see every scope.</summary>
    public bool CanSeeScope(string accessScope) => IsPlatformAdmin || Scopes.Contains(accessScope, StringComparer.Ordinal);

    public static DeNoisePrincipal ForSession(User user, string sessionId) => new(
        user.UserId, user.Username, user.DisplayName, user.RoleNames, user.Scopes, Rbac.PermissionsFor(user.RoleNames), user.MustChangePassword, CredentialKind.Session, sessionId);

    public static DeNoisePrincipal ForToken(User user, PersonalAccessToken token)
    {
        var owner = Rbac.PermissionsFor(user.RoleNames);
        var effective = token.Scopes.Length == 0 ? owner : new HashSet<string>(owner.Intersect(token.Scopes), StringComparer.Ordinal);
        // A token never bypasses a forced password change either: the account is in a restricted state.
        return new DeNoisePrincipal(user.UserId, user.Username, user.DisplayName, user.RoleNames, user.Scopes, effective, user.MustChangePassword, CredentialKind.PersonalAccessToken, TokenId: token.TokenId);
    }
}

/// <summary>Raised by services when the principal lacks a permission or the target is outside its scopes; mapped to 403 / 404 by the API.</summary>
public sealed class ForbiddenException(string permission, string? detail = null) : Exception(detail ?? $"missing permission '{permission}'")
{
    public string Permission { get; } = permission;
}

/// <summary>Target exists but is outside the caller's scopes — surfaced as 404 so existence leaks nothing (spec §22 <i>User lacks resource access</i>).</summary>
public sealed class OutOfScopeException(string targetType, Guid id) : Exception($"{targetType} {id} not found")
{
    public string TargetType { get; } = targetType;
    public Guid Id { get; } = id;
}
