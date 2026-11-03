using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace DeNoise.Integration.Tests.Support;

/// <summary>
/// A real, independent OIDC provider for the M11b scenarios: <c>navikt/mock-oauth2-server</c> (authorization code flow,
/// PKCE verification, signed id_tokens from a published JWKS, discovery per issuer). Its login form accepts arbitrary
/// claims, which stands in for Entra app-role and group assignment. Started with Testcontainers; when
/// <c>DENOISE_TEST_OIDC_URL</c> is set (an already running server, e.g. the Compose <c>mock-oidc</c> service) that is used.
/// </summary>
public sealed class MockOidcServer : IAsyncLifetime
{
    public const string Image = "ghcr.io/navikt/mock-oauth2-server:2.1.10";
    public const string ClientId = "denoise";
    private IContainer? _container;

    /// <summary>Base URL without issuer, e.g. <c>http://localhost:32768</c>.</summary>
    public string BaseUrl { get; private set; } = string.Empty;

    public string Authority(string issuerId = "entra") => $"{BaseUrl}/{issuerId}";

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("DENOISE_TEST_OIDC_URL");
        if (!string.IsNullOrWhiteSpace(external))
        {
            BaseUrl = external.TrimEnd('/');
            return;
        }
        _container = new ContainerBuilder(Image)
            .WithEnvironment("SERVER_PORT", "8080")
            .WithPortBinding(8080, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/entra/.well-known/openid-configuration")))
            .Build();
        await _container.StartAsync();
        BaseUrl = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(8080)}";
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }
}
