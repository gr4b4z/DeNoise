using System.Text;
using AlertHub.Api.Auth;
using AlertHub.Domain.Users;
using AlertHub.Infrastructure.Realtime;

namespace AlertHub.Api.Endpoints;

/// <summary><c>GET /api/v1/events/stream</c> (ADR-12, 06 §4 Realtime): scope-filtered SSE with <c>Last-Event-ID</c> replay, <c>resync</c> on gaps, <c>heartbeat</c> event every 15 s.</summary>
public static class EventsEndpoints
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    public static IEndpointRouteBuilder MapEvents(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/events/stream", async (HttpContext http, SseHub hub, TimeProvider time) =>
        {
            var principal = http.Principal();
            var requested = http.Request.Query["scope"].Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToHashSet(StringComparer.Ordinal);
            IReadOnlySet<string>? scopes = principal.IsPlatformAdmin
                ? (requested.Count == 0 ? null : requested)
                : (requested.Count == 0 ? principal.Scopes.ToHashSet(StringComparer.Ordinal) : requested.Intersect(principal.Scopes).ToHashSet(StringComparer.Ordinal));

            http.Response.StatusCode = 200;
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers.Connection = "keep-alive";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            var body = http.Response.Body;

            var lastEventId = http.Request.Headers["Last-Event-ID"].FirstOrDefault() ?? http.Request.Query["lastEventId"].FirstOrDefault();
            var (replay, live, id) = hub.Subscribe(scopes, lastEventId);
            try
            {
                await WriteAsync(body, $"retry: 3000\n: connected {hub.Generation}\n\n", http.RequestAborted);
                if (replay is null)
                {
                    await WriteAsync(body, "event: resync\ndata: {\"reason\":\"gap\"}\n\n", http.RequestAborted);
                }
                else
                {
                    foreach (var evt in replay) await WriteAsync(body, Format(hub, evt), http.RequestAborted);
                }

                while (!http.RequestAborted.IsCancellationRequested)
                {
                    using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
                    heartbeat.CancelAfter(HeartbeatInterval);
                    try
                    {
                        while (await live.WaitToReadAsync(heartbeat.Token))
                        {
                            while (live.TryRead(out var evt)) await WriteAsync(body, Format(hub, evt), http.RequestAborted);
                            break;
                        }
                    }
                    catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
                    {
                        // A named event rather than a comment: browsers do not surface comment lines to EventSource, and the FreshnessBar needs to see it (08 §1).
                        await WriteAsync(body, $"event: heartbeat\ndata: {{\"at\":\"{time.GetUtcNow():O}\"}}\n\n", http.RequestAborted);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // client went away
            }
            finally
            {
                hub.Unsubscribe(id);
            }
        }).RequireAuthorization().RequirePermission(Permissions.EpisodeRead).AddEndpointFilter<MustChangePasswordFilter>().WithName("EventStream")
          .ExcludeFromDescription();
        return app;
    }

    private static string Format(SseHub hub, SseEvent evt) => $"id: {hub.FormatId(evt.Id)}\nevent: {evt.Type}\ndata: {evt.Data}\n\n";

    private static async Task WriteAsync(Stream body, string text, CancellationToken ct)
    {
        await body.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
        await body.FlushAsync(ct);
    }
}
