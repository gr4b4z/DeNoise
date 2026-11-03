using Azure.Core;
using Azure.Identity;
using DeNoise.Application.Auth;
using Microsoft.Extensions.Options;

namespace DeNoise.Infrastructure.Auth;

/// <summary>Supplies the <c>client_assertion</c> for <c>credential: federated</c> (workload identity federation).</summary>
public interface IClientAssertionProvider
{
    Task<string> GetAssertionAsync(CancellationToken ct = default);
}

/// <summary>
/// The assertion is a managed-identity token for <c>api://AzureADTokenExchange</c>; the app registration trusts that identity
/// through a federated credential, so there is no client secret to store or rotate. On Azure Container Apps
/// <see cref="ManagedIdentityCredential"/> reads <c>IDENTITY_ENDPOINT</c>/<c>IDENTITY_HEADER</c> and caches the token.
/// </summary>
public sealed class ManagedIdentityClientAssertionProvider(IOptions<OidcAuthOptions> options) : IClientAssertionProvider
{
    private readonly Lazy<ManagedIdentityCredential> _credential = new(() =>
        string.IsNullOrWhiteSpace(options.Value.ManagedIdentityClientId)
            ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(options.Value.ManagedIdentityClientId)));

    public async Task<string> GetAssertionAsync(CancellationToken ct = default)
    {
        var scope = options.Value.FederatedTokenAudience.TrimEnd('/') + "/.default";
        try
        {
            var token = await _credential.Value.GetTokenAsync(new TokenRequestContext([scope]), ct);
            return token.Token;
        }
        catch (AuthenticationFailedException ex)
        {
            // Surfaced as provider_unavailable by the caller; the Azure.Identity message holds no secret.
            throw new InvalidOperationException("managed identity token for the federated client assertion is unavailable", ex);
        }
    }
}
