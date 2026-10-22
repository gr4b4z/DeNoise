using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Ingest;
using DeNoise.Application.Integrations;
using DeNoise.Application.Mapping;
using DeNoise.Application.Processing;
using DeNoise.Domain.Integrations;
using DeNoise.Integration.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace DeNoise.Integration.Tests.Milestone16;

/// <summary>
/// ADR-16 on the ingest host: a GitHub delivery carries no bearer, so a <em>required</em> signature is the credential — valid 202,
/// wrong or missing 401; an integration whose signature is optional keeps refusing unauthenticated requests; a redelivery with the same
/// <c>X-GitHub-Delivery</c> is a duplicate once processed.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class GithubIngestSignatureTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Secret = "github-webhook-secret";
    private static readonly HmacConfig GithubHmac = new("sha256", "X-Hub-Signature-256", "hex", Required: true);
    private static readonly byte[] Body = Encoding.UTF8.GetBytes(ReferenceMappings.GithubWorkflowRunFailureSample);
    private string _cs = string.Empty;
    private HostFactory<DeNoise.Ingest.IngestHost> _factory = null!;

    public async Task InitializeAsync()
    {
        _cs = await postgres.CreateDatabaseAsync("githubsig");
        _factory = new HostFactory<DeNoise.Ingest.IngestHost>(_cs);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<IntegrationCredentials> CreateAsync(string name, HmacConfig hmac)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<IntegrationService>()
            .CreateAsync(new CreateIntegration(name, IntegrationTypes.Github, "scope-a", HmacSecret: Secret, Hmac: hmac), Actor.System("test"));
        await scope.ServiceProvider.GetRequiredService<MappingService>().SeedReferenceAsync(created.Integration, Actor.System("test"));
        return created;
    }

    private async Task<(HttpStatusCode Status, Guid? EventId, DateTimeOffset ReceivedAt)> PostAsync(IntegrationCredentials creds, string? signature, string delivery, bool bearer = false)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(creds.IngestPath, UriKind.Relative)) { Content = new ByteArrayContent(Body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (bearer) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.IngestToken);
        request.Headers.TryAddWithoutValidation("X-GitHub-Event", "workflow_run");
        request.Headers.TryAddWithoutValidation("X-GitHub-Delivery", delivery);
        if (signature is not null) request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", signature);
        using var response = await client.SendAsync(request);
        if (response.StatusCode != HttpStatusCode.Accepted) return (response.StatusCode, null, default);
        var accepted = await response.Content.ReadFromJsonAsync<Accepted>();
        return (response.StatusCode, accepted!.EventId, accepted.ReceivedAt);
    }

    private sealed record Accepted(Guid EventId, DateTimeOffset ReceivedAt);

    private static string GithubSignature(string secret) => "sha256=" + IngestSignatureVerifier.Sign(GithubHmac, secret, Body);

    [Fact]
    public async Task Scenario_GithubDeliveryWithoutBearer_valid_signature_202_wrong_401_missing_401_and_a_redelivery_is_a_duplicate()
    {
        var creds = await CreateAsync("github-required", GithubHmac);
        var first = await PostAsync(creds, GithubSignature(Secret), "d-1");
        first.Status.Should().Be(HttpStatusCode.Accepted, "the required signature is the credential (ADR-16)");
        (await PostAsync(creds, GithubSignature("wrong-secret"), "d-2")).Status.Should().Be(HttpStatusCode.Unauthorized);
        (await PostAsync(creds, null, "d-3")).Status.Should().Be(HttpStatusCode.Unauthorized, "no bearer and no signature is nobody");
        (await PostAsync(creds, "deadbeef", "d-4")).Status.Should().Be(HttpStatusCode.Unauthorized, "a malformed signature is invalid, not missing");
        (await PostAsync(creds, null, "d-5", bearer: true)).Status.Should().Be(HttpStatusCode.Unauthorized, "with a bearer the integration's own rule still requires the signature (07 §3)");
        (await PostAsync(creds, GithubSignature(Secret), "d-6", bearer: true)).Status.Should().Be(HttpStatusCode.Accepted, "operators testing with curl can still use the bearer");

        var redelivery = await PostAsync(creds, GithubSignature(Secret), "d-1");
        redelivery.Status.Should().Be(HttpStatusCode.Accepted, "acceptance is durable and dumb; de-duplication happens at processing");

        await using var services = TestServices.Build(_cs);
        var opened = await TestServices.InScopeAsync(services, sp => sp.GetRequiredService<EventProcessor>().ProcessAsync(new NormaliseJobPayload(first.EventId!.Value, first.ReceivedAt, creds.Integration.IntegrationId)));
        opened.Outcome.Should().Be(ProcessingOutcome.Opened, opened.Detail);
        var duplicate = await TestServices.InScopeAsync(services, sp => sp.GetRequiredService<EventProcessor>().ProcessAsync(new NormaliseJobPayload(redelivery.EventId!.Value, redelivery.ReceivedAt, creds.Integration.IntegrationId)));
        duplicate.Outcome.Should().Be(ProcessingOutcome.Duplicate, "same X-GitHub-Delivery, same delivery key");
        duplicate.EpisodeId.Should().Be(opened.EpisodeId);
    }

    [Fact]
    public async Task Optional_signature_never_stands_in_for_the_bearer()
    {
        var creds = await CreateAsync("github-optional", GithubHmac with { Required = false });
        (await PostAsync(creds, GithubSignature(Secret), "o-1")).Status.Should().Be(HttpStatusCode.Unauthorized, "signature-only authentication exists only for required signatures");
        (await PostAsync(creds, null, "o-2")).Status.Should().Be(HttpStatusCode.Unauthorized);
        (await PostAsync(creds, GithubSignature(Secret), "o-3", bearer: true)).Status.Should().Be(HttpStatusCode.Accepted);
    }
}
