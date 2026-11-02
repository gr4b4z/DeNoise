using System.Diagnostics;
using System.Net;
using DeNoise.Application.Ingest;
using DeNoise.Infrastructure.Observability;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace DeNoise.Ingest;

/// <summary>
/// <c>POST /ingest/{integrationKeyId}</c> (06 §2) and, for producers that cannot set headers,
/// <c>POST /ingest/{integrationKeyId}/{credential}</c>. Status codes are exactly 202 / 401 / 413 / 429 / 503 and the
/// response body is fixed-size: no echo of input. The rate limiter answers 429 before this handler runs; both routes
/// carry <c>integrationKeyId</c>, so they share one partition.
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
        // The token in the URL is accepted only for an integration that carries the path_credential capability.
        endpoints.MapPost("/ingest/{integrationKeyId}/{credential}", HandleAsync)
            .RequireRateLimiting(RateLimitPolicy)
            .WithName("IngestWithPathCredential")
            .DisableAntiforgery();
        return endpoints;
    }

    private static async Task<Results<Accepted<IngestAcceptedResponse>, UnauthorizedHttpResult, StatusCodeHttpResult>> HandleAsync(
        string integrationKeyId, HttpContext http, IIngestAuthenticator authenticator, IngestService ingest, IIngestSignatureVerifier signatures,
        IOptions<IngestOptions> options, DeNoiseMetrics metrics, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("DeNoise.Ingest");
        var limits = options.Value;

        // Bearer token per integration (06 §2); without one, ADR-16: the webhook signature is the credential, but only for an
        // integration that requires a signature and has a secret — the verdict check below then refuses anything but Valid.
        // A header-less producer may instead put the token in the path, which only a path_credential integration accepts.
        Domain.Integrations.Integration? integration;
        var bearer = BearerToken(http.Request);
        // A header beats the URL: a bearer sent to the path route is the stronger credential, so it is used as such.
        var pathCredential = bearer is null ? PathCredential(http) : null;
        try
        {
            integration = (bearer, pathCredential) switch
            {
                (not null, _) => await authenticator.AuthenticateAsync(integrationKeyId, bearer, ct),
                (null, not null) => await ResolveFromPathAsync(authenticator, integrationKeyId, pathCredential, ct),
                _ => await authenticator.ResolveSignedOnlyAsync(integrationKeyId, ct),
            };
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

        // The URL carries the credential in this mode, so the configured allow list is applied here (and only here).
        if (pathCredential is not null && !IngestCredentialPolicy.PermitsAddress(integration.IpAllowList, http.Connection.RemoteIpAddress))
        {
            metrics.IngestRejected.Add(1, new KeyValuePair<string, object?>("reason", "ip_not_allowed"));
            logger.LogWarning("Source address refused by the allow list for integration {IntegrationId} (trace {TraceId})",
                integration.IntegrationId, Activity.Current?.TraceId.ToString());
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
        var credentialled = bearer is not null || pathCredential is not null;
        if (verdict == SignatureVerdict.Invalid || (verdict == SignatureVerdict.Missing && hmac.Required) || (!credentialled && verdict != SignatureVerdict.Valid))
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

    /// <summary>The credential segment of <c>/ingest/{integrationKeyId}/{credential}</c>; null on the header route.</summary>
    private static string? PathCredential(HttpContext http)
        => http.Request.RouteValues.TryGetValue("credential", out var value) && value?.ToString() is { Length: > 0 } credential
            ? credential
            : null;

    /// <summary>
    /// Verifies the token exactly as the header route does, then drops the integration when it has not opted in.
    /// The order matters: an integration without the capability must cost the same verification as a wrong token,
    /// so the response cannot be used to tell the two apart.
    /// </summary>
    private static async Task<Domain.Integrations.Integration?> ResolveFromPathAsync(
        IIngestAuthenticator authenticator, string integrationKeyId, string credential, CancellationToken ct)
    {
        var integration = await authenticator.AuthenticateAsync(integrationKeyId, credential, ct);
        return integration is not null && IngestCredentialPolicy.AllowsPathCredential(integration) ? integration : null;
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
