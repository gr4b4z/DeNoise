using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DeNoise.Infrastructure.Health;

public static class HealthEndpoints
{
    public const string LiveTag = "live";
    public const string ReadyTag = "ready";
    public const string StartupTag = "startup";

    /// <summary><c>/healthz/live</c>, <c>/healthz/ready</c>, <c>/healthz/startup</c> on every host (06 §8, B8).</summary>
    public static IEndpointRouteBuilder MapDeNoiseHealth(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/healthz/live", Options(LiveTag));
        endpoints.MapHealthChecks("/healthz/ready", Options(ReadyTag));
        endpoints.MapHealthChecks("/healthz/startup", Options(StartupTag));
        return endpoints;
    }

    private static HealthCheckOptions Options(string tag) => new()
    {
        Predicate = r => r.Tags.Contains(tag),
        ResultStatusCodes =
        {
            [HealthStatus.Healthy] = StatusCodes.Status200OK,
            [HealthStatus.Degraded] = StatusCodes.Status200OK,
            [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
        },
        ResponseWriter = WriteAsync,
    };

    private static Task WriteAsync(HttpContext ctx, HealthReport report)
    {
        ctx.Response.ContentType = "application/json";
        var body = new
        {
            status = report.Status.ToString().ToLowerInvariant(),
            totalDurationMs = (int)report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString().ToLowerInvariant(),
                description = e.Value.Description,
                durationMs = (int)e.Value.Duration.TotalMilliseconds,
                data = e.Value.Data.Count == 0 ? null : e.Value.Data,
            }),
        };
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(body, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };
}
