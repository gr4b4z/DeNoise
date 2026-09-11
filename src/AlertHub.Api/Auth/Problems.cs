using System.Text.Json;
using AlertHub.Application.Abstractions;

namespace AlertHub.Api.Auth;

/// <summary>RFC 9457 problem details with the <c>urn:alerthub:error:*</c> type family and a trace id (06 §1).</summary>
public static class Problems
{
    public const string ContentType = "application/problem+json";

    public static object Body(HttpContext http, int status, string code, string title, string? detail, object? extra = null)
    {
        var dict = new Dictionary<string, object?>
        {
            ["type"] = $"urn:alerthub:error:{code}",
            ["status"] = status,
            ["title"] = title,
            ["detail"] = detail,
            ["traceId"] = http.CorrelationId(),
            ["instance"] = http.Request.Path.Value,
        };
        if (extra is not null)
        {
            foreach (var p in JsonSerializer.SerializeToElement(extra, JsonDefaults.Stored).EnumerateObject()) dict[p.Name] = p.Value;
        }
        return dict;
    }

    public static IResult Result(HttpContext http, int status, string code, string title, string? detail, object? extra = null)
        => Results.Json(Body(http, status, code, title, detail, extra), JsonDefaults.Stored, ContentType, status);

    public static Task WriteAsync(HttpContext http, int status, string code, string title, string? detail, object? extra = null)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = ContentType;
        return http.Response.WriteAsync(JsonSerializer.Serialize(Body(http, status, code, title, detail, extra), JsonDefaults.Stored));
    }
}
