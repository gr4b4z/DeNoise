using System.Diagnostics;
using System.Net;
using AlertHub.Application.Ingest;
using AlertHub.Infrastructure.Observability;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace AlertHub.Ingest;

/// <summary>
/// <c>POST /ingest/{integrationKeyId}</c> (06 §2). Status codes are exactly 202 / 401 / 413 / 429 / 503 and the
/// response body is fixed-size: no echo of input. The rate limiter answers 429 before this handler runs.
/// </summary>
public static class IngestEndpoints
{
    public const string RateLimitPolicy = "ingest";

    public static IEndpointRouteBuilder MapIngest(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/ingest/{integrationKeyId}", HandleAsync)
            .RequireRateLimiting(RateLimitPolicy)
            .WithName("Ingest")
            .DisableAntiforgery();
        return endpoints;
    }

    private static async Task<Results<Accepted<IngestAcceptedResponse>, UnauthorizedHttpResult, StatusCodeHttpResult>> HandleAsync(
        string integrationKeyId, HttpContext http, IIngestAuthenticator authenticator, IngestService ingest, IIngestSignatureVerifier signatures,
        IOptions<IngestOptions> options, AlertHubMetrics metrics, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("AlertHub.Ingest");
        var limits = options.Value;

        Domain.Integrations.Integration? integration;
        try
        {
            integration = await authenticator.AuthenticateAsync(integrationKeyId, BearerToken(http.Request), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Integration lookup failed for key {KeyId} (trace {TraceId})", integrationKeyId, Activity.Current?.TraceId.ToString());
            metrics.IngestRejected.Add(1, new KeyValuePair<string, object?>("reason", "not_durable"));
            http.Response.Headers.RetryAfter = "5";
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        if (integration is null)
        {
            metrics.IngestRejected.Add(1, new KeyValuePair<string, object?>("reason", "unauthorized"));
            return TypedResults.Unauthorized();
        }

        if (http.Request.ContentLength is > 0 && http.Request.ContentLength > limits.PayloadBytes)
        {
            metrics.IngestRejected.Add(1, new KeyValuePair<string, object?>("reason", "too_large"));
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        byte[] body;
        try
        {
            body = await ReadBoundedAsync(http.Request.Body, limits.PayloadBytes, ct);
        }
        catch (PayloadTooLargeException)
        {
            metrics.IngestRejected.Add(1, new KeyValuePair<string, object?>("reason", "too_large"));
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in limits.StoredHeaders)
        {
            if (http.Request.Headers.TryGetValue(name, out var values) && values.Count > 0)
            {
                headers[name] = string.Join(",", values!);
            }
        }

        // Webhook signature (Atlas X-MMS-Signature and alike, 06 §2): an invalid signature is always refused; a missing one only when required.
        var hmac = HmacConfig.Parse(integration.HmacConfig) ?? new HmacConfig();
        var signatureHeaders = http.Request.Headers.TryGetValue(hmac.Header, out var sig) && sig.Count > 0
            ? new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase) { [hmac.Header] = sig.ToString() }
            : headers;
        var verdict = signatures.Verify(integration, body, signatureHeaders);
        if (verdict == SignatureVerdict.Invalid || (verdict == SignatureVerdict.Missing && hmac.Required))
        {
            metrics.IngestRejected.Add(1, new KeyValuePair<string, object?>("reason", verdict == SignatureVerdict.Invalid ? "bad_signature" : "missing_signature"));
            logger.LogWarning("Signature {Verdict} for integration {IntegrationId} (trace {TraceId})", verdict, integration.IntegrationId, Activity.Current?.TraceId.ToString());
            return TypedResults.Unauthorized();
        }

        try
        {
            var accepted = await ingest.AcceptAsync(integration, new IngestRequest(body, http.Request.ContentType, headers, http.Connection.RemoteIpAddress), ct);
            metrics.IngestAccepted.Add(1, new KeyValuePair<string, object?>("integration", integration.IntegrationId.ToString()));
            return TypedResults.Accepted((string?)null, new IngestAcceptedResponse(accepted.EventId, accepted.ReceivedAt));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Durable acceptance failed: nothing was committed, so the source must retry (spec §15.1).
            logger.LogError(ex, "Durable acceptance failed for integration {IntegrationId} (trace {TraceId})", integration.IntegrationId, Activity.Current?.TraceId.ToString());
            metrics.IngestRejected.Add(1, new KeyValuePair<string, object?>("reason", "not_durable"));
            http.Response.Headers.RetryAfter = "5";
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static string? BearerToken(HttpRequest request)
    {
        var header = request.Headers[HeaderNames.Authorization].ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && header.Length > prefix.Length
            ? header[prefix.Length..].Trim()
            : null;
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream body, int limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit) throw new PayloadTooLargeException();
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private sealed class PayloadTooLargeException : Exception;
}

public sealed record IngestAcceptedResponse(Guid EventId, DateTimeOffset ReceivedAt);
