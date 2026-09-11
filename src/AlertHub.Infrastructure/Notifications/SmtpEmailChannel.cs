using System.Text;
using System.Text.Json.Nodes;
using AlertHub.Application.Notifications;
using AlertHub.Domain.Notifications;
using AlertHub.Domain.Ops;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace AlertHub.Infrastructure.Notifications;

public sealed class SmtpOptions
{
    public const string Section = "Smtp";
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string From { get; set; } = "alert-hub@example.invalid";
    public string? Username { get; set; }
    public string? Password { get; set; }
    /// <summary><c>Auto</c> (default), <c>None</c> (Mailpit/dev), <c>StartTls</c>, <c>SslOnConnect</c>.</summary>
    public string Security { get; set; } = "Auto";
    /// <summary>Domain used in generated <c>Message-ID</c>s.</summary>
    public string MessageIdDomain { get; set; } = "alerthub.local";
}

/// <summary>
/// SMTP e-mail (06 §7): subject <c>[AlertHub][{severity}] {summary} — {resourceName}</c>, plain-text body, <c>Message-ID</c>
/// derived from the outbox id so receivers can deduplicate, <c>References</c> linking updates to the opening mail.
/// Payload text is rendered as text — never as HTML (AGENTS.md rule 8).
/// </summary>
public sealed class SmtpEmailChannel(IOptions<SmtpOptions> options, ILogger<SmtpEmailChannel> logger) : INotificationChannel
{
    public string ChannelType => ChannelTypes.SmtpEmail;

    public async Task<ChannelResult> SendAsync(ResolvedDestination destination, OutboxMessage message, string body, CancellationToken ct)
    {
        var smtp = options.Value;
        if (string.IsNullOrWhiteSpace(smtp.Host)) return ChannelResult.Retryable("SMTP host not configured (Smtp:Host)");
        var recipients = destination.Destination.EmailTo ?? [];
        if (recipients.Length == 0) return ChannelResult.Permanent("destination has no recipients");

        var mime = Compose(message, body, recipients, smtp);
        try
        {
            using var client = new SmtpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(destination.Destination.Timeout);
            var security = smtp.Security switch
            {
                "None" => SecureSocketOptions.None,
                "StartTls" => SecureSocketOptions.StartTls,
                "SslOnConnect" => SecureSocketOptions.SslOnConnect,
                _ => SecureSocketOptions.Auto,
            };
            await client.ConnectAsync(smtp.Host, smtp.Port, security, timeout.Token);
            if (!string.IsNullOrEmpty(smtp.Username))
            {
                await client.AuthenticateAsync(smtp.Username, smtp.Password ?? string.Empty, timeout.Token);
            }
            var response = await client.SendAsync(mime, timeout.Token);
            await client.DisconnectAsync(true, CancellationToken.None);
            return ChannelResult.Success(null, response);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ChannelResult.ResponseLost("SMTP conversation timed out");
        }
        catch (SmtpCommandException ex) when (ex.StatusCode is >= MailKit.Net.Smtp.SmtpStatusCode.MailboxUnavailable and < (MailKit.Net.Smtp.SmtpStatusCode)600 && (int)ex.StatusCode >= 500)
        {
            logger.LogDebug(ex, "SMTP permanent failure");
            return ChannelResult.Permanent($"SMTP {(int)ex.StatusCode}: {ex.Message}", (int)ex.StatusCode);
        }
        catch (Exception ex) when (ex is SmtpCommandException or SmtpProtocolException or System.Net.Sockets.SocketException or IOException)
        {
            logger.LogDebug(ex, "SMTP transient failure");
            return ChannelResult.Retryable(ex.GetType().Name + ": " + ex.Message);
        }
    }

    public static MimeMessage Compose(OutboxMessage message, string body, string[] recipients, SmtpOptions smtp)
    {
        var model = JsonNode.Parse(body) as JsonObject ?? new JsonObject();
        var episode = model["episode"] as JsonObject;
        var severity = episode?["severity"]?.ToString() ?? "unknown";
        var summary = episode?["summary"]?.ToString() ?? message.Type;
        var resource = episode?["resource"]?["name"]?.ToString();

        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(smtp.From));
        foreach (var to in recipients) mime.To.Add(MailboxAddress.Parse(to));
        mime.Subject = $"[AlertHub][{severity}] {summary}" + (string.IsNullOrEmpty(resource) ? string.Empty : $" — {resource}");
        mime.MessageId = MessageId(message.OutboxId, smtp.MessageIdDomain);
        mime.Headers.Add("X-AlertHub-Delivery-Id", message.OutboxId.ToString());
        mime.Headers.Add("X-AlertHub-Event", message.Type);
        if (message.Type != NotificationTypes.EpisodeOpened && message.EpisodeId is { } episodeId)
        {
            // Threads every update under the episode: receivers group by References even without the opening mail's id.
            mime.References.Add(MessageId(episodeId, smtp.MessageIdDomain));
        }
        else if (message.EpisodeId is { } opened)
        {
            mime.MessageId = MessageId(opened, smtp.MessageIdDomain);
        }
        mime.Body = new TextPart("plain") { Text = PlainText(message, model) };
        return mime;
    }

    public static string MessageId(Guid id, string domain) => $"{id:N}@{domain}";

    private static string PlainText(OutboxMessage message, JsonObject model)
    {
        var sb = new StringBuilder();
        var episode = model["episode"] as JsonObject;
        sb.Append("Alert Hub notification: ").AppendLine(message.Type);
        sb.AppendLine();
        if (episode is not null)
        {
            void Line(string label, string key)
            {
                var v = episode[key];
                if (v is not null && v.GetValueKind() != System.Text.Json.JsonValueKind.Null) sb.Append(label).Append(": ").AppendLine(v is JsonValue ? v.ToString() : v.ToJsonString());
            }
            Line("Severity", "severity");
            Line("Summary", "summary");
            Line("Condition", "conditionState");
            Line("Handling", "handlingState");
            if (episode["resource"]?["name"] is { } rn) sb.Append("Resource: ").AppendLine(rn.ToString());
            if (episode["rule"]?["name"] is { } rl) sb.Append("Rule: ").AppendLine(rl.ToString());
            Line("Service", "service");
            Line("Environment", "environment");
            if (episode["owningTeam"]?["name"] is { } team) sb.Append("Owning team: ").AppendLine(team.ToString());
            Line("First seen", "firstSeen");
            Line("Last seen", "lastSeen");
            Line("Occurrences", "occurrenceCount");
            if (episode["closure"] is JsonObject closure)
            {
                sb.Append("Closure: ").Append(closure["reason"]).Append(" (evidence: ").Append(closure["evidence"]).AppendLine(")");
            }
            sb.AppendLine();
            Line("Open in Alert Hub", "url");
            Line("Source", "sourceUrl");
            Line("Runbook", "runbookUrl");
        }
        else if (model["hub"] is JsonObject hub)
        {
            sb.AppendLine(hub.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        sb.AppendLine();
        sb.Append("Delivery id: ").AppendLine(message.OutboxId.ToString());
        return sb.ToString();
    }
}
