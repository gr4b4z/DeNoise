using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DeNoise.Contract.Tests;

/// <summary>
/// The generated OpenAPI document is the executable contract (06). These tests guard drift between the running
/// implementation, the committed snapshot the frontend generator consumes (<c>docs/openapi/v1.json</c>) and the
/// security rules; they run without a database. Regenerate the snapshot with <c>UPDATE_OPENAPI=1 dotnet test tests/DeNoise.Contract.Tests</c>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class OpenApiContractTests : IAsyncLifetime
{
    private WebApplicationFactory<DeNoise.Api.ApiHost> _factory = null!;
    private JsonDocument _document = null!;
    private string _json = string.Empty;
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<DeNoise.Api.ApiHost>().WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:DeNoise", "Host=localhost;Port=1;Database=x;Username=x;Password=x;Timeout=1"));
        using var client = _factory.CreateClient();
        _json = await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        _document = JsonDocument.Parse(_json);
    }

    public async Task DisposeAsync()
    {
        _document.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public void Document_is_openapi_3_and_names_the_api()
    {
        _document.RootElement.GetProperty("openapi").GetString().Should().StartWith("3.");
        _document.RootElement.GetProperty("info").GetProperty("title").GetString().Should().Be("DeNoise application API");
    }

    [Fact]
    public void Every_non_health_non_anonymous_operation_declares_a_security_scheme()
    {
        var schemes = _document.RootElement.GetProperty("components").GetProperty("securitySchemes");
        schemes.TryGetProperty("session", out _).Should().BeTrue();
        schemes.TryGetProperty("pat", out _).Should().BeTrue();

        var anonymous = new HashSet<string> { "/auth/login", "/auth/providers", "/auth/reset/request" };
        foreach (var path in _document.RootElement.GetProperty("paths").EnumerateObject())
        {
            if (path.Name.StartsWith("/healthz", StringComparison.Ordinal) || path.Name.StartsWith("/openapi", StringComparison.Ordinal)) continue;
            foreach (var op in path.Value.EnumerateObject())
            {
                if (anonymous.Contains(path.Name)) continue;
                op.Value.TryGetProperty("security", out var security).Should().BeTrue($"{op.Name.ToUpperInvariant()} {path.Name} must require authentication");
                security.GetArrayLength().Should().BeGreaterThan(0, $"{op.Name.ToUpperInvariant()} {path.Name}");
            }
        }
    }

    [Fact]
    public void Contract_covers_the_06_endpoint_families()
    {
        var paths = _document.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
        paths.Should().Contain(["/auth/login", "/auth/logout", "/auth/csrf", "/auth/change-password", "/api/v1/me", "/api/v1/me/tokens", "/api/v1/me/sessions",
            "/api/v1/episodes", "/api/v1/episodes/{id}", "/api/v1/episodes/{id}/timeline", "/api/v1/episodes/{id}/ack", "/api/v1/episodes/{id}/close", "/api/v1/episodes/bulk",
            "/api/v1/users", "/api/v1/teams", "/api/v1/teams/{id}/overview", "/api/v1/integrations", "/api/v1/policies/{kind}", "/api/v1/destinations"]);
    }

    [Fact]
    public void Committed_snapshot_matches_the_running_api()
    {
        var repoRoot = FindRepoRoot();
        var snapshotPath = Path.Combine(repoRoot, "docs", "openapi", "v1.json");
        var pretty = JsonSerializer.Serialize(_document.RootElement, Pretty);
        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI") == "1" || !File.Exists(snapshotPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
            File.WriteAllText(snapshotPath, pretty + "\n");
        }
        var committed = File.ReadAllText(snapshotPath).TrimEnd();
        committed.Should().Be(pretty, "docs/openapi/v1.json is out of date; run UPDATE_OPENAPI=1 dotnet test tests/DeNoise.Contract.Tests and commit the result");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DeNoise.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
