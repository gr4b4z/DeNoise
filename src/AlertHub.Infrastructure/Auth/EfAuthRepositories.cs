using AlertHub.Application.Auth;
using AlertHub.Domain.Users;
using AlertHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlertHub.Infrastructure.Auth;

public sealed class EfUserRepository(AlertHubDbContext db) : IUserRepository
{
    public Task<User?> GetAsync(Guid userId, CancellationToken ct = default) => db.Users.SingleOrDefaultAsync(u => u.UserId == userId, ct);
    public Task<User?> FindByUsernameAsync(string username, CancellationToken ct = default)
    {
        var normalised = username.Trim().ToLowerInvariant();
        return db.Users.SingleOrDefaultAsync(u => u.Username == normalised, ct);
    }
    public Task<User?> FindByEmailAsync(string email, CancellationToken ct = default) => db.Users.SingleOrDefaultAsync(u => u.Email == email, ct);
    public async Task<IReadOnlyList<User>> ListAsync(string? query, int limit, CancellationToken ct = default)
    {
        var q = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var like = $"%{query.Trim()}%";
            q = q.Where(u => EF.Functions.ILike(u.Username, like) || EF.Functions.ILike(u.DisplayName, like) || (u.Email != null && EF.Functions.ILike(u.Email, like)));
        }
        return await q.OrderBy(u => u.Username).Take(limit).ToListAsync(ct);
    }
    public Task<int> CountAsync(CancellationToken ct = default) => db.Users.CountAsync(ct);
    public void Add(User user) => db.Users.Add(user);
}

public sealed class EfSessionRepository(AlertHubDbContext db) : ISessionRepository
{
    public Task<Session?> GetAsync(string sessionId, CancellationToken ct = default) => db.Sessions.SingleOrDefaultAsync(s => s.SessionId == sessionId, ct);
    public async Task<IReadOnlyList<Session>> ListActiveForUserAsync(Guid userId, DateTimeOffset now, CancellationToken ct = default)
        => await db.Sessions.AsNoTracking().Where(s => s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now && s.AbsoluteExpiresAt > now).OrderByDescending(s => s.LastSeenAt).ToListAsync(ct);
    public void Add(Session session) => db.Sessions.Add(session);
    public Task<int> RevokeAllAsync(Guid userId, string? exceptSessionId, DateTimeOffset now, CancellationToken ct = default)
        => db.Sessions.Where(s => s.UserId == userId && s.RevokedAt == null && (exceptSessionId == null || s.SessionId != exceptSessionId))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAt, now), ct);
}

public sealed class EfPersonalAccessTokenRepository(AlertHubDbContext db) : IPersonalAccessTokenRepository
{
    public Task<PersonalAccessToken?> FindByKeyIdAsync(string keyId, CancellationToken ct = default) => db.PersonalAccessTokens.AsNoTracking().SingleOrDefaultAsync(t => t.KeyId == keyId, ct);
    public Task<PersonalAccessToken?> GetAsync(Guid tokenId, CancellationToken ct = default) => db.PersonalAccessTokens.SingleOrDefaultAsync(t => t.TokenId == tokenId, ct);
    public async Task<IReadOnlyList<PersonalAccessToken>> ListForUserAsync(Guid userId, CancellationToken ct = default)
        => await db.PersonalAccessTokens.AsNoTracking().Where(t => t.UserId == userId && t.RevokedAt == null).OrderByDescending(t => t.CreatedAt).ToListAsync(ct);
    public void Add(PersonalAccessToken token) => db.PersonalAccessTokens.Add(token);
    public Task TouchLastUsedAsync(Guid tokenId, DateTimeOffset at, CancellationToken ct = default)
        => db.PersonalAccessTokens.Where(t => t.TokenId == tokenId).ExecuteUpdateAsync(u => u.SetProperty(t => t.LastUsedAt, at), ct);
}

public sealed class EfLoginAttemptRepository(AlertHubDbContext db) : ILoginAttemptRepository
{
    public void Add(LoginAttempt attempt) => db.LoginAttempts.Add(attempt);
    public Task<int> CountFailuresAsync(string? username, System.Net.IPAddress? ip, DateTimeOffset since, CancellationToken ct = default)
    {
        var q = db.LoginAttempts.Where(a => !a.Success && a.At >= since);
        if (username is not null) q = q.Where(a => a.Username == username);
        if (ip is not null) q = q.Where(a => a.Ip == ip);
        return q.CountAsync(ct);
    }
}

public sealed class EfIdempotencyStore(AlertHubDbContext db) : IIdempotencyStore
{
    public Task<IdempotencyRecord?> GetAsync(Guid userId, string key, CancellationToken ct = default)
        => db.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(r => r.UserId == userId && r.Key == key, ct);

    public async Task<bool> TryStoreAsync(IdempotencyRecord record, CancellationToken ct = default)
    {
        var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO cfg.idempotency_key (user_id, key, request_fingerprint, status_code, response_body, created_at, expires_at)
            VALUES ({record.UserId}, {record.Key}, {record.RequestFingerprint}, {record.StatusCode}, {record.ResponseBody}, {record.CreatedAt}, {record.ExpiresAt})
            ON CONFLICT (user_id, key) DO NOTHING
            """, ct);
        return rows == 1;
    }
}

public sealed class EfTeamMemberRepository(AlertHubDbContext db) : ITeamMemberRepository
{
    public async Task<IReadOnlyList<TeamMember>> ListForUserAsync(Guid userId, CancellationToken ct = default) => await db.TeamMembers.AsNoTracking().Where(m => m.UserId == userId).ToListAsync(ct);
    public async Task<IReadOnlyList<TeamMember>> ListForTeamAsync(Guid teamId, CancellationToken ct = default) => await db.TeamMembers.AsNoTracking().Where(m => m.TeamId == teamId).ToListAsync(ct);
    public void Add(TeamMember member) => db.TeamMembers.Add(member);
    public Task<int> RemoveAsync(Guid teamId, Guid userId, CancellationToken ct = default) => db.TeamMembers.Where(m => m.TeamId == teamId && m.UserId == userId).ExecuteDeleteAsync(ct);
}

public sealed class EfSavedFilterRepository(AlertHubDbContext db) : ISavedFilterRepository
{
    public async Task<IReadOnlyList<SavedFilter>> ListForUserAsync(Guid userId, CancellationToken ct = default) => await db.SavedFilters.AsNoTracking().Where(f => f.UserId == userId).OrderBy(f => f.Name).ToListAsync(ct);
    public Task<SavedFilter?> GetAsync(Guid filterId, CancellationToken ct = default) => db.SavedFilters.SingleOrDefaultAsync(f => f.FilterId == filterId, ct);
    public void Add(SavedFilter filter) => db.SavedFilters.Add(filter);
    public void Remove(SavedFilter filter) => db.SavedFilters.Remove(filter);
}
