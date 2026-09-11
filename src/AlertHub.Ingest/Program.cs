using System.Threading.RateLimiting;
using AlertHub.Application.Ingest;
using AlertHub.Infrastructure;
using AlertHub.Infrastructure.Health;
using AlertHub.Infrastructure.Hosting;
using AlertHub.Infrastructure.Observability;
using AlertHub.Ingest;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.AddAlertHubCore("ingest");
builder.Services.AddAlertHubInfrastructure(builder.Configuration);

// Runs behind the AKS ingress: trust X-Forwarded-For/Proto for source_ip and rate-limit keys.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

// Kestrel-level cap slightly above the configured payload limit so the handler can answer 413 itself.
builder.WebHost.ConfigureKestrel((ctx, kestrel) =>
{
    var limits = ctx.Configuration.GetSection(IngestOptions.Section).Get<IngestOptions>() ?? new IngestOptions();
    kestrel.Limits.MaxRequestBodySize = limits.PayloadBytes + 64 * 1024;
});

// Per-integration sliding window (06 §2: default 600/min, Retry-After on 429). Partitioned by key id so one
// noisy producer cannot starve another; the heartbeat ping path gets its own bucket in milestone 6.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = (ctx, _) =>
    {
        ctx.HttpContext.Response.Headers.RetryAfter = ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry)
            ? ((int)Math.Ceiling(retry.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "10";
        ctx.HttpContext.RequestServices.GetRequiredService<AlertHubMetrics>().IngestRejected
            .Add(1, new KeyValuePair<string, object?>("reason", "rate_limited"));
        return ValueTask.CompletedTask;
    };
    o.AddPolicy(IngestEndpoints.RateLimitPolicy, ctx =>
    {
        var key = ctx.Request.RouteValues.TryGetValue("integrationKeyId", out var k) ? k?.ToString() ?? string.Empty : string.Empty;
        var perMinute = ctx.RequestServices.GetRequiredService<IOptions<IngestOptions>>().Value.IngestPerMinute;
        return RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = perMinute,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });
});

var app = builder.Build();
app.UseForwardedHeaders();
app.UseAlertHubRequestLogging();
app.UseRateLimiter();
app.MapAlertHubHealth();
app.MapIngest();

app.Run();

namespace AlertHub.Ingest
{
    /// <summary>Assembly marker for <c>WebApplicationFactory</c> in tests.</summary>
    public sealed class IngestHost;
}
