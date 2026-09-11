using AlertHub.Domain.Users;

namespace AlertHub.Application.Auth;

public interface IUserRepository
{
    Task<User?> GetAsync(Guid userId, CancellationToken ct = default);
    Task<User?> FindByUsernameAsync(string username, CancellationToken ct = default);
    Task<User?> FindByEmailAsync(string email, CancellationToken ct = default);
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
/// Proves identity. <c>LocalPasswordProvider</c> is v1; <c>OidcProvider</c> arrives in milestone 11b. Authorization stays
/// local regardless (ADR-14).
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
