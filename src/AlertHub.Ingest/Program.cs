using AlertHub.Infrastructure.Health;
using AlertHub.Infrastructure.Hosting;
using AlertHub.Infrastructure.Observability;

var builder = WebApplication.CreateBuilder(args);
builder.AddAlertHubCore("ingest");

var app = builder.Build();
app.UseAlertHubRequestLogging();
app.MapAlertHubHealth();

app.Run();

namespace AlertHub.Ingest
{
    /// <summary>Assembly marker for <c>WebApplicationFactory</c> in tests.</summary>
    public sealed class IngestHost;
}
