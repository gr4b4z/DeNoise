using System.Net;
using System.Net.Http.Json;
using AlertHub.Integration.Tests.Support;

namespace AlertHub.Integration.Tests.Milestone0;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class HealthEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private string _cs = string.Empty;

    public async Task InitializeAsync() => _cs = await postgres.CreateDatabaseAsync("health");
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Api_host_reports_live_ready_and_startup()
    {
        await using var factory = new HostFactory<AlertHub.Api.ApiHost>(_cs);
        using var client = factory.CreateClient();
        foreach (var path in new[] { "/healthz/live", "/healthz/ready", "/healthz/startup" })
        {
            using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
            response.StatusCode.Should().Be(HttpStatusCode.OK, path);
        }

        using var ready = await client.GetAsync(new Uri("/healthz/ready", UriKind.Relative));
        var body = await ready.Content.ReadFromJsonAsync<HealthBody>();
        body!.Status.Should().Be("healthy");
        body.Checks.Select(c => c.Name).Should().Contain(["database", "outbox-lag"]);
    }

    [Fact]
    public async Task Api_host_serves_openapi_document()
    {
        await using var factory = new HostFactory<AlertHub.Api.ApiHost>(_cs);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"openapi\"");
    }

    [Fact]
    public async Task Ready_is_503_when_database_is_unreachable()
    {
        var bad = new Npgsql.NpgsqlConnectionStringBuilder(_cs) { Host = "127.0.0.1", Port = 1, Timeout = 1 }.ConnectionString;
        await using var factory = new HostFactory<AlertHub.Api.ApiHost>(bad);
        using var client = factory.CreateClient();
        using var live = await client.GetAsync(new Uri("/healthz/live", UriKind.Relative));
        live.StatusCode.Should().Be(HttpStatusCode.OK);
        using var ready = await client.GetAsync(new Uri("/healthz/ready", UriKind.Relative));
        ready.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    private sealed record HealthBody(string Status, List<HealthCheckEntry> Checks);
    private sealed record HealthCheckEntry(string Name, string Status);
}
