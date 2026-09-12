using DeNoise.Domain.Integrations;

namespace DeNoise.Application.Ingest;

/// <summary>Resolves the integration for an ingest request. Unknown key, wrong token and inactive integration are indistinguishable to the caller (401).</summary>
public interface IIngestAuthenticator
{
    Task<Integration?> AuthenticateAsync(string ingestKeyId, string? bearerToken, CancellationToken ct = default);
}
