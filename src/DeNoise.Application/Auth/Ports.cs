using DeNoise.Domain.Users;

namespace DeNoise.Application.Auth;

public interface IUserRepository
{
    Task<User?> GetAsync(Guid userId, CancellationToken ct = default);
    Task<User?> FindByUsernameAsync(string username, CancellationToken ct = default);
    Task<User?> FindByEmailAsync(string email, CancellationToken ct = default);
    /// <summary>Every user whose e-mail equals <paramref name="email"/> ignoring case (SSO matching must see case variants, not pick one).</summary>
    Task<IReadOnlyList<User>> ListByEmailIgnoreCaseAsync(string email, CancellationToken ct = default);
    Task<User?> FindByExternalIdentityAsync(string issuer, string externalId, CancellationToken ct = default);
    Task<IReadOnlyList<User>> ListAsync(string? query, int limit, CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
    void Add(User user);
}

public interface ISessionRepository
{
    Task<Session?> GetAsync(string sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<Session>> ListActiveForUserAsync(Guid userId, DateTimeOffset now, CancellationToken ct = default);
    void Add(Session session);
    /// <summary>Revokes every active session of the user except <paramref name="exceptSessionId"/>; returns the count.</summary>
    Task<int> RevokeAllAsync(Guid userId, string? exceptSessionId, DateTimeOffset now, CancellationToken ct = default);
}

public interface IPersonalAccessTokenRepository
{
    Task<PersonalAccessToken?> FindByKeyIdAsync(string keyId, CancellationToken ct = default);
    Task<PersonalAccessToken?> GetAsync(Guid tokenId, CancellationToken ct = default);
    Task<IReadOnlyList<PersonalAccessToken>> ListForUserAsync(Guid userId, CancellationToken ct = default);
    void Add(PersonalAccessToken token);
    Task TouchLastUsedAsync(Guid tokenId, DateTimeOffset at, CancellationToken ct = default);
}

public interface ILoginAttemptRepository
{
    void Add(LoginAttempt attempt);
    Task<int> CountFailuresAsync(string? username, System.Net.IPAddress? ip, DateTimeOffset since, CancellationToken ct = default);
}

public interface IIdempotencyStore
{
    Task<IdempotencyRecord?> GetAsync(Guid userId, string key, CancellationToken ct = default);
    /// <summary>Stores the response; returns false when another request already stored the key (race), in which case the caller replays that one.</summary>
    Task<bool> TryStoreAsync(IdempotencyRecord record, CancellationToken ct = default);
}

public interface ITeamMemberRepository
{
    Task<IReadOnlyList<TeamMember>> ListForUserAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<TeamMember>> ListForTeamAsync(Guid teamId, CancellationToken ct = default);
    void Add(TeamMember member);
    Task<int> RemoveAsync(Guid teamId, Guid userId, CancellationToken ct = default);
}

public interface ISavedFilterRepository
{
    Task<IReadOnlyList<SavedFilter>> ListForUserAsync(Guid userId, CancellationToken ct = default);
    Task<SavedFilter?> GetAsync(Guid filterId, CancellationToken ct = default);
    void Add(SavedFilter filter);
    void Remove(SavedFilter filter);
}

/// <summary>
/// Proves identity with a username and a password (<c>LocalPasswordProvider</c>). Redirect-based providers use
/// <see cref="IExternalIdentityProvider"/> instead. Authorization stays locally enforced regardless (ADR-14, ADR-15).
/// </summary>
public interface IIdentityProvider
{
    string Name { get; }
    Task<AuthenticationResult> AuthenticateAsync(string username, string password, System.Net.IPAddress? ip, CancellationToken ct = default);
}

public enum AuthenticationStatus
{
    Success,
    /// <summary>Unknown user, wrong password or disabled account — all indistinguishable to the caller.</summary>
    InvalidCredentials,
    Locked,
}

public sealed record AuthenticationResult(AuthenticationStatus Status, User? User = null, DateTimeOffset? LockedUntil = null);

/// <summary>
/// Proves identity through a redirect to an external identity provider (OIDC authorization code + PKCE, M11b). The provider
/// only proves who the caller is and reports the raw role and group claims; projecting them onto the local user and issuing
/// the session is <see cref="ExternalSignInService"/>'s job, so the session, CSRF, SSE and PAT paths stay as they are.
/// </summary>
public interface IExternalIdentityProvider
{
    string Name { get; }
    bool Enabled { get; }

    /// <summary>Builds the authorization request. <paramref name="returnTo"/> must already be a validated local path (<see cref="ReturnToPolicy"/>).</summary>
    Task<ExternalChallenge> BuildChallengeAsync(string returnTo, CancellationToken ct = default);

    /// <summary>Validates the callback against the state cookie, redeems the code and validates the id_token. Never throws for protocol failures.</summary>
    Task<ExternalAuthResult> CompleteAsync(ExternalCallback callback, CancellationToken ct = default);
}

/// <summary>Where to send the browser, and the protected state (state, nonce, PKCE verifier, returnTo) the caller keeps in a short-lived HttpOnly cookie.</summary>
public sealed record ExternalChallenge(Uri AuthorizationUrl, string StateCookie, DateTimeOffset ExpiresAt);

/// <summary>What the identity provider sent back, plus the state cookie the browser presented. None of it is trusted.</summary>
public sealed record ExternalCallback(string? Code, string? State, string? Error, string? ErrorDescription, string? StateCookie);

/// <summary>
/// A verified external identity. <see cref="Subject"/> is the Entra object id (<c>oid</c>), stable across applications;
/// <see cref="Roles"/> and <see cref="Groups"/> are the raw claim values, not yet mapped.
/// </summary>
public sealed record ExternalPrincipal(
    string Issuer, string Subject, string? Email, string? DisplayName, string? PreferredUsername,
    IReadOnlyList<string> Roles, IReadOnlyList<string> Groups, bool GroupsOverage = false, bool IsGuest = false);

/// <summary>Either a principal, or a refusal reason from <see cref="OidcRefusalReasons"/>. <see cref="ReturnTo"/> is always a safe local path.</summary>
public sealed record ExternalAuthResult(ExternalPrincipal? Principal, string ReturnTo, string? FailureReason = null, string? FailureDetail = null, string? Subject = null)
{
    public bool Succeeded => Principal is not null;
    public static ExternalAuthResult Fail(string reason, string? detail = null, string? subject = null, string returnTo = ReturnToPolicy.Default) => new(null, returnTo, reason, detail, subject);
}

/// <summary>
/// Reasons recorded on <c>auth.oidc_refused</c>. Every one of them is answered to the browser with the same redirect
/// (<c>/login?error=sso</c>), so the audit distinguishes what the caller cannot.
/// </summary>
public static class OidcRefusalReasons
{
    public const string Disabled = "oidc_disabled";
    public const string StateMissing = "state_missing";
    public const string StateInvalid = "state_invalid";
    public const string StateExpired = "state_expired";
    public const string StateReplayed = "state_replayed";
    public const string IdpError = "idp_error";
    public const string CodeMissing = "code_missing";
    public const string CodeRejected = "code_rejected";
    public const string ProviderUnavailable = "provider_unavailable";
    public const string InvalidSignature = "invalid_signature";
    public const string InvalidIssuer = "invalid_issuer";
    public const string InvalidAudience = "invalid_audience";
    public const string TokenExpired = "token_expired";
    public const string TokenNotYetValid = "token_not_yet_valid";
    public const string InvalidToken = "invalid_token";
    public const string NonceMismatch = "nonce_mismatch";
    public const string MissingSubject = "missing_subject";
    public const string NoMappedRole = "no_mapped_role";
    public const string NotProvisioned = "not_provisioned";
    public const string UserDisabled = "user_disabled";
    public const string EmailConflict = "email_conflict";
}
