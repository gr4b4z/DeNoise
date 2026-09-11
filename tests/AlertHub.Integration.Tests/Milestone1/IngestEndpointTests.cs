using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Ingest;
using AlertHub.Application.Integrations;
using AlertHub.Domain.Ops;
using AlertHub.Infrastructure.Persistence;
using AlertHub.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlertHub.Integration.Tests.Milestone1;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class IngestEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private string _cs = string.Empty;
    private HostFactory<AlertHub.Ingest.IngestHost> _factory = null!;
    private IntegrationCredentials _credentials = null!;

    public async Task InitializeAsync()
    {
        _cs = await postgres.CreateDatabaseAsync("ingest");
        _factory = new HostFactory<AlertHub.Ingest.IngestHost>(_cs, settings: new Dictionary<string, string?>
        {
            ["Limits:PayloadBytes"] = "4096",
            ["Limits:IngestPerMinute"] = "5",
        });
        _credentials = await CreateIntegrationAsync();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<IntegrationCredentials> CreateIntegrationAsync(string name = "generic")
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IntegrationService>();
        return await service.CreateAsync(new CreateIntegration(name, "generic_webhook", "scope-a"), Actor.System("test"));
    }

    private static HttpRequestMessage Post(IntegrationCredentials creds, string? token, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(creds.IngestPath, UriKind.Relative)) { Content = content };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task Valid_token_returns_202_and_commits_raw_event_with_normalise_job()
    {
        using var client = _factory.CreateClient();
        var body = """{"eventType":"firing","alertId":"orders-api-5xx"}""";
        using var request = Post(_credentials, _credentials.IngestToken, new StringContent(body, Encoding.UTF8, "application/json"));
        request.Headers.TryAddWithoutValidation("X-AlertHub-Source-Time", "2026-09-11T10:00:00Z");
        request.Headers.TryAddWithoutValidation("X-Secret-Header", "must-not-be-stored");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var accepted = await response.Content.ReadFromJsonAsync<Accepted>();
        accepted!.EventId.Should().NotBeEmpty();

        await using var db = PostgresFixture.CreateContext(_cs);
        var raw = await db.RawEvents.SingleAsync(r => r.EventId == accepted.EventId);
        raw.IntegrationId.Should().Be(_credentials.Integration.IntegrationId);
        Encoding.UTF8.GetString(raw.Body).Should().Be(body);
        raw.SizeBytes.Should().Be(Encoding.UTF8.GetByteCount(body));
        raw.ContentType.Should().StartWith("application/json");
        raw.Headers.Should().Contain("X-AlertHub-Source-Time").And.NotContain("X-Secret-Header");

        var job = await db.Jobs.SingleAsync(j => j.IntegrationId == _credentials.Integration.IntegrationId);
        job.Kind.Should().Be(JobKinds.Normalise);
        job.Status.Should().Be(JobStatus.Pending);
        var payload = JsonSerializer.Deserialize<NormaliseJobPayload>(job.Payload, Web);
        payload!.EventId.Should().Be(accepted.EventId);
        payload.ReceivedAt.Should().BeCloseTo(raw.ReceivedAt, TimeSpan.FromMilliseconds(1));
    }

    [Theory]
    [InlineData("wrong-token")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Wrong_or_missing_token_returns_401_and_stores_nothing(string? token)
    {
        using var client = _factory.CreateClient();
        using var response = await client.SendAsync(Post(_credentials, token, new StringContent("{}", Encoding.UTF8, "application/json")));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();

        await using var db = PostgresFixture.CreateContext(_cs);
        (await db.RawEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Unknown_key_id_returns_401()
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsync(new Uri("/ingest/abcdefghjkmn", UriKind.Relative), new StringContent("{}"));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Rotated_token_invalidates_the_old_one_and_the_new_one_works()
    {
        var fresh = await CreateIntegrationAsync("rotate-me");
        IntegrationCredentials rotated;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            rotated = await scope.ServiceProvider.GetRequiredService<IntegrationService>()
                .RotateIngestTokenAsync(fresh.Integration.IntegrationId, Actor.System("test"));
        }

        using var client = _factory.CreateClient();
        using var old = await client.SendAsync(Post(fresh, fresh.IngestToken, new StringContent("{}")));
        old.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var oldKeyNewToken = await client.SendAsync(Post(fresh, rotated.IngestToken, new StringContent("{}")));
        oldKeyNewToken.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var fresh202 = await client.SendAsync(Post(rotated, rotated.IngestToken, new StringContent("{}")));
        fresh202.StatusCode.Should().Be(HttpStatusCode.Accepted);
        rotated.Integration.Version.Should().Be(2);

        await using var db = PostgresFixture.CreateContext(_cs);
        (await db.AuditEntries.CountAsync(a => a.Action == "integration.rotate_ingest_token")).Should().Be(1);
    }

    [Fact]
    public async Task Disabled_integration_returns_401()
    {
        var creds = await CreateIntegrationAsync("disable-me");
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IntegrationService>()
                .SetActiveAsync(creds.Integration.IntegrationId, false, Actor.System("test"), "test");
        }
        using var client = _factory.CreateClient();
        using var response = await client.SendAsync(Post(creds, creds.IngestToken, new StringContent("{}")));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Oversized_body_returns_413()
    {
        var creds = await CreateIntegrationAsync("big");
        using var client = _factory.CreateClient();
        var big = new string('x', 5000);
        using var response = await client.SendAsync(Post(creds, creds.IngestToken, new StringContent(big)));
        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Random_bytes_and_invalid_json_are_accepted_never_500()
    {
        var creds = await CreateIntegrationAsync("bytes");
        using var client = _factory.CreateClient();
        foreach (var body in new[] { RandomNumberGenerator.GetBytes(1024), Encoding.UTF8.GetBytes("not json {{{"), Encoding.UTF8.GetBytes(new string('[', 2000)) })
        {
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var response = await client.SendAsync(Post(creds, creds.IngestToken, content));
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }
    }

    [Fact]
    public async Task Requests_over_the_per_integration_limit_get_429_with_retry_after()
    {
        var creds = await CreateIntegrationAsync("busy");
        using var client = _factory.CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 7; i++)
        {
            using var response = await client.SendAsync(Post(creds, creds.IngestToken, new StringContent("{}")));
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                response.Headers.RetryAfter.Should().NotBeNull();
            }
        }
        statuses.Count(s => s == HttpStatusCode.Accepted).Should().Be(5);
        statuses.Count(s => s == HttpStatusCode.TooManyRequests).Should().Be(2);
    }

    [Fact]
    public async Task Database_unavailable_returns_503_not_202()
    {
        var bad = new Npgsql.NpgsqlConnectionStringBuilder(_cs) { Host = "127.0.0.1", Port = 1, Timeout = 1 }.ConnectionString;
        await using var factory = new HostFactory<AlertHub.Ingest.IngestHost>(bad);
        using var client = factory.CreateClient();
        using var response = await client.SendAsync(Post(_credentials, _credentials.IngestToken, new StringContent("{}")));
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter.Should().NotBeNull();
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed record Accepted(Guid EventId, DateTimeOffset ReceivedAt);
}
