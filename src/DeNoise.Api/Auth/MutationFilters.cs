using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Auth;
using DeNoise.Domain.Users;

namespace DeNoise.Api.Auth;

/// <summary><c>If-Match: "&lt;version&gt;"</c> is mandatory on mutating episode/heartbeat/config endpoints; missing ⇒ 428 (06 §1). The parsed version lands in <c>HttpContext.Items["IfMatch"]</c>.</summary>
public sealed class IfMatchFilter : IEndpointFilter
{
    public const string ItemKey = "DeNoise.IfMatch";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var header = http.Request.Headers.IfMatch.ToString().Trim().Trim('"');
        if (header.Length == 0)
        {
            return Problems.Result(http, StatusCodes.Status428PreconditionRequired, "precondition-required", "If-Match required", "Send the episode version you last saw as If-Match: \"<version>\".");
        }
        if (!int.TryParse(header, out var version))
        {
            return Problems.Result(http, StatusCodes.Status400BadRequest, "invalid-if-match", "Invalid If-Match", "If-Match must be the integer version, quoted.");
        }
        http.Items[ItemKey] = version;
        return await next(context);
    }

    public static int Version(HttpContext http) => (int)http.Items[ItemKey]!;
}

/// <summary>
/// <c>Idempotency-Key</c> (UUID) required on every POST action (06 §1). The first response is stored 24 h per user and
/// replayed for the same key; the same key with a different request is rejected (422).
/// </summary>
public sealed class IdempotencyFilter(IIdempotencyStore store, TimeProvider time, Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json) : IEndpointFilter
{
    public const string HeaderName = "Idempotency-Key";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var key = http.Request.Headers[HeaderName].ToString().Trim();
        if (!Guid.TryParse(key, out var keyGuid))
        {
            return Problems.Result(http, StatusCodes.Status400BadRequest, "idempotency-key-required", "Idempotency-Key required", "Every action needs an Idempotency-Key header containing a UUID.");
        }
        var principal = http.Principal();
        var fingerprint = await FingerprintAsync(http);
        var existing = await store.GetAsync(principal.UserId, keyGuid.ToString("N"), http.RequestAborted);
        if (existing is not null)
        {
            if (existing.RequestFingerprint != fingerprint)
            {
                return Problems.Result(http, StatusCodes.Status422UnprocessableEntity, "idempotency-key-reused", "Idempotency-Key reused", "This key was already used for a different request.");
            }
            http.Response.Headers["Idempotent-Replayed"] = "true";
            return existing.ResponseBody is null
                ? Results.StatusCode(existing.StatusCode)
                : Results.Content(existing.ResponseBody, "application/json", Encoding.UTF8, existing.StatusCode);
        }

        var result = await next(context);
        var (status, body) = await CaptureAsync(result, http, json.Value.SerializerOptions);
        if (status is >= 200 and < 300 || status == StatusCodes.Status409Conflict)
        {
            var now = time.GetUtcNow();
            await store.TryStoreAsync(new IdempotencyRecord
            {
                UserId = principal.UserId,
                Key = keyGuid.ToString("N"),
                RequestFingerprint = fingerprint,
                StatusCode = status,
                ResponseBody = body,
                CreatedAt = now,
                ExpiresAt = now.AddHours(24),
            }, http.RequestAborted);
        }
        return result;
    }

    private static async Task<string> FingerprintAsync(HttpContext http)
    {
        http.Request.EnableBuffering();
        using var ms = new MemoryStream();
        await http.Request.Body.CopyToAsync(ms, http.RequestAborted);
        http.Request.Body.Position = 0;
        var material = Encoding.UTF8.GetBytes(http.Request.Method + " " + http.Request.Path + "\n").Concat(ms.ToArray()).ToArray();
        return Convert.ToHexStringLower(SHA256.HashData(material));
    }

    /// <summary>Extracts status + JSON body from the typed results our handlers return so they can be replayed byte-for-byte.</summary>
    private static async Task<(int Status, string? Body)> CaptureAsync(object? result, HttpContext http, JsonSerializerOptions options)
    {
        switch (result)
        {
            case IStatusCodeHttpResult { StatusCode: { } sc } and IValueHttpResult { Value: var value }:
                return (sc, value is null ? null : JsonSerializer.Serialize(value, value.GetType(), options));
            case IStatusCodeHttpResult { StatusCode: { } sc }:
                return (sc, null);
            case IValueHttpResult { Value: var value }:
                return (200, value is null ? null : JsonSerializer.Serialize(value, value.GetType(), options));
            default:
                await Task.CompletedTask;
                return (http.Response.StatusCode, null);
        }
    }
}
