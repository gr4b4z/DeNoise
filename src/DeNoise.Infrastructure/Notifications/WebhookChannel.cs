using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using DeNoise.Application.Notifications;
using DeNoise.Domain.Notifications;
using DeNoise.Domain.Ops;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeNoise.Infrastructure.Notifications;

/// <summary>
/// Outbound webhook (ADR-7, 06 §7): signed, per-delivery id stable across retries, redirects not followed, response
/// classified into success / retryable / permanent / response_lost, first 4 KiB of the response kept for debugging.
/// </summary>
public sealed class WebhookChannel(IHttpClientFactory httpClients, IOptions<NotificationOptions> options, TimeProvider time, ILogger<WebhookChannel> logger) : INotificationChannel
{
    public const string HttpClientName = "denoise-webhook";
    public const int ResponseExcerptBytes = 4096;

    public string ChannelType => ChannelTypes.Webhook;

    public async Task<ChannelResult> SendAsync(ResolvedDestination destination, OutboxMessage message, string body, CancellationToken ct)
    {
        if (destination.Url is null || !Uri.TryCreate(destination.Url, UriKind.Absolute, out var uri))
        {
            return ChannelResult.Permanent("destination has no valid URL");
        }
        if (uri.Scheme != Uri.UriSchemeHttps && !(options.Value.AllowInsecureDestinations && uri.Scheme == Uri.UriSchemeHttp))
        {
            return ChannelResult.Permanent("destination URL must be https");
        }

        var timestamp = time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(new HttpMethod(destination.Destination.Method), uri)
        {
            Content = new StringContent(body, Encoding.UTF8, destination.ContentType),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", options.Value.UserAgent);
        request.Headers.TryAddWithoutValidation("X-DeNoise-Delivery-Id", message.OutboxId.ToString());
        request.Headers.TryAddWithoutValidation("X-DeNoise-Event", message.Type);
        request.Headers.TryAddWithoutValidation("X-DeNoise-Timestamp", timestamp);
        if (destination.SigningSecret is { Length: > 0 } secret)
        {
            request.Headers.TryAddWithoutValidation("X-DeNoise-Signature", "v1=" + Sign(secret, timestamp, body));
        }
        foreach (var (name, value) in destination.Headers)
        {
            if (!request.Headers.TryAddWithoutValidation(name, value)) request.Content.Headers.TryAddWithoutValidation(name, value);
        }

        var client = httpClients.CreateClient(HttpClientName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(destination.Destination.Timeout);
        var sent = false;
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            sent = true;
            var excerpt = await ReadExcerptAsync(response, timeout.Token);
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode) return ChannelResult.Success(status, excerpt);

            var retryAfter = RetryAfter(response.Headers.RetryAfter);
            return IsRetryable(response.StatusCode)
                ? ChannelResult.Retryable($"HTTP {status}", status, excerpt, retryAfter)
                : ChannelResult.Permanent($"HTTP {status}", status, excerpt);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timed out. If headers never arrived we cannot know whether the receiver got the request (spec §17.3).
            return ChannelResult.ResponseLost(sent ? "timed out reading the response body" : $"no response within {destination.Destination.Timeout.TotalSeconds:F0}s");
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug(ex, "Webhook to {Host} failed", uri.Host);
            return ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError
                ? ChannelResult.Retryable(ex.HttpRequestError + ": " + ex.Message)
                : ChannelResult.ResponseLost(ex.HttpRequestError + ": " + ex.Message);
        }
    }

    /// <summary>Receiver recipe (06 §7): <c>hex(hmac_sha256(secret, timestamp + "." + rawBody))</c>.</summary>
    public static string Sign(string secret, string timestamp, string body)
        => Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(timestamp + "." + body)));

    public static bool IsRetryable(HttpStatusCode status)
        => (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status == 425;

    private static TimeSpan? RetryAfter(RetryConditionHeaderValue? header)
    {
        if (header is null) return null;
        if (header.Delta is { } delta) return delta;
        if (header.Date is { } date) return date - DateTimeOffset.UtcNow;
        return null;
    }

    private static async Task<string?> ReadExcerptAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[ResponseExcerptBytes];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)) > 0)
        {
            total += read;
        }
        return total == 0 ? null : Encoding.UTF8.GetString(buffer, 0, total);
    }
}
