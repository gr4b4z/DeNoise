using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DeNoise.Contract.Tests;

/// <summary>
/// The Api image serves the SPA from <c>wwwroot</c> (build/Dockerfile): client routes fall back to <c>index.html</c>, server
/// prefixes never do, and a host without a built SPA behaves exactly as before. Runs without a database.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SpaHostingTests : IAsyncLifetime
{
    private const string ConnectionString = "Host=localhost;Port=1;Database=x;Username=x;Password=x;Timeout=1";
    private const string IndexMarker = "<title>DeNoise SPA test</title>";
    private readonly DirectoryInfo _webRoot = Directory.CreateTempSubdirectory("denoise-wwwroot-");
    private WebApplicationFactory<DeNoise.Api.ApiHost> _withSpa = null!;
    private WebApplicationFactory<DeNoise.Api.ApiHost> _withoutSpa = null!;

    public Task InitializeAsync()
    {
        File.WriteAllText(Path.Combine(_webRoot.FullName, "index.html"), $"<!doctype html><html><head>{IndexMarker}</head><body></body></html>");
        Directory.CreateDirectory(Path.Combine(_webRoot.FullName, "assets"));
        File.WriteAllText(Path.Combine(_webRoot.FullName, "assets", "index-abc123.js"), "console.log('spa');");

        _withSpa = new WebApplicationFactory<DeNoise.Api.ApiHost>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:DeNoise", ConnectionString);
            b.UseWebRoot(_webRoot.FullName);
        });
        _withoutSpa = new WebApplicationFactory<DeNoise.Api.ApiHost>().WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:DeNoise", ConnectionString));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _withSpa.DisposeAsync();
        await _withoutSpa.DisposeAsync();
        _webRoot.Delete(recursive: true);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/queue")]
    [InlineData("/episodes/0b7c8f6e-1d2a-4c55-9f00-6a1f2b3c4d5e")]
    [InlineData("/apis-are-not-reserved")]
    public async Task Client_routes_return_index_html_without_caching(string path)
    {
        using var client = _withSpa.CreateClient();
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        (await response.Content.ReadAsStringAsync()).Should().Contain(IndexMarker);
        response.Headers.CacheControl!.NoCache.Should().BeTrue();
    }

    [Fact]
    public async Task Fingerprinted_assets_are_served_as_immutable()
    {
        using var client = _withSpa.CreateClient();
        using var response = await client.GetAsync(new Uri("/assets/index-abc123.js", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.ToString().Should().Contain("immutable");
    }

    [Theory]
    [InlineData("/assets/missing.js")]
    [InlineData("/api/v1/does-not-exist")]
    [InlineData("/API/v1/does-not-exist")]
    [InlineData("/auth/does-not-exist")]
    [InlineData("/healthz/does-not-exist")]
    [InlineData("/openapi/does-not-exist")]
    public async Task Missing_files_and_server_prefixes_never_fall_back_to_the_spa(string path)
    {
        using var client = _withSpa.CreateClient();
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(IndexMarker);
    }

    [Fact]
    public async Task Api_routes_keep_working_next_to_the_spa()
    {
        using var client = _withSpa.CreateClient();
        using var openApi = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        using var wrongMethod = await client.DeleteAsync(new Uri("/auth/login", UriKind.Relative));

        openApi.StatusCode.Should().Be(HttpStatusCode.OK);
        openApi.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        wrongMethod.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task Without_a_built_spa_the_host_starts_and_unknown_paths_stay_404()
    {
        using var client = _withoutSpa.CreateClient();
        using var root = await client.GetAsync(new Uri("/", UriKind.Relative));
        using var route = await client.GetAsync(new Uri("/queue", UriKind.Relative));

        root.StatusCode.Should().Be(HttpStatusCode.NotFound);
        route.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
