using System.Text;
using AlertHub.Application.Heartbeats;
using AlertHub.Domain.Heartbeats;
using Microsoft.Extensions.Options;

namespace AlertHub.Ingest;

/// <summary>
/// <c>GET|POST /hb/{keyId}.{token}</c>, <c>/start</c>, <c>/fail</c>, <c>/exit/{code}</c> (06 §3). 200 <c>{"ok":true}</c> after commit,
/// 404 for unknown key or wrong token (indistinguishable), 429 per key from the rate limiter. The body (≤ 16 KiB, tail kept) is the run output.
/// </summary>
public static class HeartbeatPingEndpoints
{
    public const string RateLimitPolicy = "hb";
    private static readonly string[] GetOrPost = ["GET", "POST"];

    public static IEndpointRouteBuilder MapHeartbeatPings(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/hb").RequireRateLimiting(RateLimitPolicy).DisableAntiforgery();
        group.MapMethods("/{credential}", GetOrPost, (string credential, HttpContext http, HeartbeatPingService pings, IOptions<HeartbeatOptions> options, CancellationToken ct)
            => PingAsync(credential, RunKinds.Success, null, http, pings, options.Value, ct)).WithName("HeartbeatPing");
        group.MapPost("/{credential}/start", (string credential, HttpContext http, HeartbeatPingService pings, IOptions<HeartbeatOptions> options, CancellationToken ct)
            => PingAsync(credential, RunKinds.Start, null, http, pings, options.Value, ct)).WithName("HeartbeatStart");
        group.MapPost("/{credential}/fail", (string credential, HttpContext http, HeartbeatPingService pings, IOptions<HeartbeatOptions> options, CancellationToken ct)
            => PingAsync(credential, RunKinds.Fail, null, http, pings, options.Value, ct)).WithName("HeartbeatFail");
        group.MapPost("/{credential}/exit/{code:int}", (string credential, int code, HttpContext http, HeartbeatPingService pings, IOptions<HeartbeatOptions> options, CancellationToken ct)
            => PingAsync(credential, RunKinds.Exit, code, http, pings, options.Value, ct)).WithName("HeartbeatExit");
        return endpoints;
    }

    /// <summary>Rate-limit partition: the non-secret key id prefix of the credential (06 §3 "per-key limit").</summary>
    public static string PartitionKey(HttpContext ctx)
    {
        var credential = ctx.Request.RouteValues.TryGetValue("credential", out var c) ? c?.ToString() ?? string.Empty : string.Empty;
        var dot = credential.IndexOf('.');
        return dot > 0 ? credential[..dot] : credential;
    }

    private static async Task<IResult> PingAsync(string credential, string kind, int? exitCode, HttpContext http, HeartbeatPingService pings, HeartbeatOptions options, CancellationToken ct)
    {
        string? body = null;
        if (http.Request.Method == "POST" && http.Request.ContentLength is not 0)
        {
            body = await ReadTailAsync(http.Request.Body, options.BodyBytes * 4, ct); // read a bounded amount; the service keeps the tail
        }
        var ok = await pings.PingAsync(credential, kind, exitCode, body, http.Connection.RemoteIpAddress, ct);
        return ok ? Results.Json(new { ok = true }) : Results.NotFound();
    }

    private static async Task<string?> ReadTailAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        var buffer = new char[4096];
        var sb = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            sb.Append(buffer, 0, read);
            if (sb.Length > maxBytes) sb.Remove(0, sb.Length - maxBytes);
        }
        return sb.Length == 0 ? null : sb.ToString();
    }
}
