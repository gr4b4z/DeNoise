using System.Threading.RateLimiting;
using DeNoise.Application.Ingest;
using DeNoise.Infrastructure;
using DeNoise.Infrastructure.Health;
using DeNoise.Infrastructure.Hosting;
using DeNoise.Infrastructure.Observability;
using DeNoise.Ingest;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.AddDeNoiseCore("ingest");
builder.Services.AddDeNoiseInfrastructure(builder.Configuration);

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
        ctx.HttpContext.RequestServices.GetRequiredService<DeNoiseMetrics>().IngestRejected
            .Add(1, new KeyValuePair<string, object?>("reason", "rate_limited"));
        return ValueTask.CompletedTask;
    };
    // 06 §3: heartbeat pings are limited per key id (default 60/min); the token lives in the URL, so the key prefix is the partition.
    o.AddPolicy(HeartbeatPingEndpoints.RateLimitPolicy, ctx =>
    {
        var perMinute = ctx.RequestServices.GetRequiredService<IOptions<DeNoise.Application.Heartbeats.HeartbeatOptions>>().Value.PingsPerMinutePerKey;
        return RateLimitPartition.GetSlidingWindowLimiter("hb:" + HeartbeatPingEndpoints.PartitionKey(ctx), _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = perMinute,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });
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
app.UseDeNoiseRequestLogging();
app.UseRateLimiter();
app.MapDeNoiseHealth();
app.MapIngest();
app.MapHeartbeatPings();

app.Run();

namespace DeNoise.Ingest
{
    /// <summary>Assembly marker for <c>WebApplicationFactory</c> in tests.</summary>
    public sealed class IngestHost;
}
