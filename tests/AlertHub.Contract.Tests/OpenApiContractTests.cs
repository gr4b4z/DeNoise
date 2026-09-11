using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AlertHub.Contract.Tests;

/// <summary>
/// The generated OpenAPI document is the executable contract (06). These tests guard drift between the
/// document and what the frontend generator expects; they run without a database.
/// </summary>
[Trait("Category", "Unit")]
public sealed class OpenApiContractTests
{
    [Fact]
    public async Task OpenApi_document_is_served_and_names_the_api()
    {
        await using var factory = new WebApplicationFactory<AlertHub.Api.ApiHost>().WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:AlertHub", "Host=localhost;Port=1;Database=x;Username=x;Password=x;Timeout=1"));
        using var client = factory.CreateClient();
        var json = await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("openapi").GetString().Should().StartWith("3.");
        doc.RootElement.GetProperty("info").GetProperty("title").GetString().Should().NotBeNullOrEmpty();
    }
}
