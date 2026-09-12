using System.Security.Cryptography;
using System.Text;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Ingest;
using DeNoise.Application.Integrations;
using DeNoise.Domain.Integrations;
using Microsoft.Extensions.Caching.Memory;

namespace DeNoise.Infrastructure.Ingest;

/// <summary>
/// Looks up the current integration version by key id and verifies the bearer token against its Argon2id hash.
/// Successful verifications are cached for a short time keyed by SHA-256(keyId + version + token) so a busy
/// producer does not pay the Argon2 cost on every request; rotation creates a new version, which changes the key.
/// Failures are never cached, and unknown key / wrong token / inactive integration all return null.
/// </summary>
public sealed class IntegrationIngestAuthenticator(IIntegrationRepository integrations, ISecretHasher hasher, IMemoryCache cache) : IIngestAuthenticator
{
    public static readonly TimeSpan VerificationCacheTtl = TimeSpan.FromMinutes(5);

    public async Task<Integration?> AuthenticateAsync(string ingestKeyId, string? bearerToken, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(bearerToken) || ingestKeyId.Length != TokenGenerator.KeyIdLength) return null;

        var integration = await integrations.FindCurrentByIngestKeyAsync(ingestKeyId, ct);
        if (integration is null || !integration.Active) return null;

        var cacheKey = CacheKey(ingestKeyId, bearerToken, integration.Version);
        if (cache.TryGetValue(cacheKey, out _)) return integration;

        if (!hasher.Verify(bearerToken, integration.IngestTokenHash)) return null;

        cache.Set(cacheKey, true, VerificationCacheTtl);
        return integration;
    }

    private static string CacheKey(string keyId, string token, int version)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{keyId}{version}{token}"));
        return "ingest-auth:" + Convert.ToHexStringLower(digest);
    }
}
