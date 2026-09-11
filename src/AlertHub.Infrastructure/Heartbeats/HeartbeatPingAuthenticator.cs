using System.Security.Cryptography;
using System.Text;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Heartbeats;
using AlertHub.Application.Integrations;
using AlertHub.Domain.Heartbeats;
using Microsoft.Extensions.Caching.Memory;

namespace AlertHub.Infrastructure.Heartbeats;

/// <summary>
/// Key-id lookup + Argon2id verification of the URL token (06 §3). Unknown key and wrong token both cost one verification
/// (a dummy hash is checked for unknown keys) so timing does not reveal which; successes are cached per (key, rotation, token).
/// </summary>
public sealed class HeartbeatPingAuthenticator(IHeartbeatRepository heartbeats, ISecretHasher hasher, IMemoryCache cache) : IHeartbeatPingAuthenticator
{
    public static readonly TimeSpan VerificationCacheTtl = TimeSpan.FromMinutes(5);
    private static string? _dummyHash;

    public async Task<Heartbeat?> AuthenticateAsync(string keyId, string token, CancellationToken ct = default)
    {
        if (keyId.Length != TokenGenerator.KeyIdLength || token.Length is < 16 or > 128) return null;
        var hb = await heartbeats.FindByKeyIdAsync(keyId, ct);
        if (hb is null)
        {
            hasher.Verify(token, _dummyHash ??= hasher.HashToken(TokenGenerator.NewSecret()));
            return null;
        }
        var cacheKey = "hb-auth:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{keyId}{hb.TokenRotatedAt:O}{token}")));
        if (cache.TryGetValue(cacheKey, out _)) return hb;
        if (!hasher.Verify(token, hb.TokenHash)) return null;
        cache.Set(cacheKey, true, VerificationCacheTtl);
        return hb;
    }
}
