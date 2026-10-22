using DeNoise.Domain.Integrations;

namespace DeNoise.Application.Ingest;

/// <summary>Resolves the integration for an ingest request. Unknown key, wrong token and inactive integration are indistinguishable to the caller (401).</summary>
public interface IIngestAuthenticator
{
    Task<Integration?> AuthenticateAsync(string ingestKeyId, string? bearerToken, CancellationToken ct = default);

    /// <summary>
    /// ADR-16 "signature as credential": a request without a bearer token may still be authenticated by its webhook signature, but
    /// only for an active integration whose HMAC verification is configured <c>required</c> with a secret. Returns that candidate
    /// (the signature itself is verified by the caller over the raw body) or null, which the caller treats as unauthorised.
    /// </summary>
    Task<Integration?> ResolveSignedOnlyAsync(string ingestKeyId, CancellationToken ct = default);
}
