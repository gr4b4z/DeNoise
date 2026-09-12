using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Ingest;
using AlertHub.Application.Integrations;
using AlertHub.Integration.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace AlertHub.Integration.Tests.Milestone8;

/// <summary>06 §2: Atlas <c>X-MMS-Signature</c> over the raw body on the ingest host — invalid is always 401, missing only when required.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AtlasIngestSignatureTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Secret = "atlas-webhook-secret";
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"id":"a-1","status":"OPEN","groupId":"g","alertConfigId":"c","eventTypeName":"HOST_DOWN","created":"2026-09-11T10:00:00Z"}""");
    private string _cs = string.Empty;
    private HostFactory<AlertHub.Ingest.IngestHost> _factory = null!;

    public async Task InitializeAsync()
    {
        _cs = await postgres.CreateDatabaseAsync("atlassig");
        _factory = new HostFactory<AlertHub.Ingest.IngestHost>(_cs);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<IntegrationCredentials> CreateAsync(string name, HmacConfig? hmac, string? secret)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IntegrationService>()
            .CreateAsync(new CreateIntegration(name, "atlas", "scope-a", HmacSecret: secret, Hmac: hmac), Actor.System("test"));
    }

    private async Task<HttpStatusCode> PostAsync(IntegrationCredentials creds, HmacConfig hmac, string? signature)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(creds.IngestPath, UriKind.Relative)) { Content = new ByteArrayContent(Body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.IngestToken);
        if (signature is not null) request.Headers.TryAddWithoutValidation(hmac.Header, signature);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    [Fact]
    public async Task Required_signature_valid_202_invalid_401_missing_401()
    {
        var hmac = new HmacConfig("sha1", "X-MMS-Signature", "base64", Required: true);
        var creds = await CreateAsync("atlas-required", hmac, Secret);
        (await PostAsync(creds, hmac, IngestSignatureVerifier.Sign(hmac, Secret, Body))).Should().Be(HttpStatusCode.Accepted);
        (await PostAsync(creds, hmac, IngestSignatureVerifier.Sign(hmac, "wrong-secret", Body))).Should().Be(HttpStatusCode.Unauthorized);
        (await PostAsync(creds, hmac, null)).Should().Be(HttpStatusCode.Unauthorized, "required and absent");
    }

    [Fact]
    public async Task Optional_signature_lets_unsigned_requests_through_but_still_refuses_a_wrong_one()
    {
        var hmac = new HmacConfig("sha256", "X-Hook-Signature", "hex", Required: false);
        var creds = await CreateAsync("atlas-optional", hmac, Secret);
        (await PostAsync(creds, hmac, null)).Should().Be(HttpStatusCode.Accepted, "verify-before-enable phase (07 §3)");
        (await PostAsync(creds, hmac, IngestSignatureVerifier.Sign(hmac, Secret, Body))).Should().Be(HttpStatusCode.Accepted);
        (await PostAsync(creds, hmac, "deadbeef")).Should().Be(HttpStatusCode.Unauthorized, "a presented signature must verify");
    }

    [Fact]
    public async Task Integration_without_a_secret_ignores_signature_headers()
    {
        var creds = await CreateAsync("atlas-plain", null, null);
        (await PostAsync(creds, new HmacConfig(), "anything")).Should().Be(HttpStatusCode.Accepted);
    }
}
