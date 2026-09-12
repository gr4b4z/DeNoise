using System.Text.Json;
using System.Text.Json.Nodes;
using DeNoise.Application.Abstractions;
using DeNoise.Domain.Alerts;
using DeNoise.Domain.Common;
using DeNoise.Domain.Episodes;
using DeNoise.Domain.Integrations;
using DeNoise.Domain.Notifications;
using DeNoise.Domain.Teams;

namespace DeNoise.Application.Notifications;

/// <summary>
/// Builds the notification model available to templates and sent as the <c>generic-json</c> body (06 §7).
/// Text fields are raw strings; the JSON encoder escapes them, so payload content can never break the document.
/// <c>deliveryId</c> and <c>sentAt</c> are filled by the dispatcher at send time.
/// </summary>
public static class NotificationModel
{
    public static JsonObject ForEpisode(string eventType, Episode episode, NormalisedEvent? evt, Integration integration, Team? team, string publicBaseUrl, object? escalation = null, object? previous = null)
    {
        var model = new JsonObject
        {
            ["event"] = eventType,
            ["deliveryId"] = null,
            ["sentAt"] = null,
            ["episode"] = new JsonObject
            {
                ["id"] = episode.EpisodeId.ToString(),
                ["url"] = $"{publicBaseUrl.TrimEnd('/')}/episodes/{episode.EpisodeId}",
                ["severity"] = episode.Severity.ToWire(),
                ["summary"] = episode.Summary,
                ["conditionState"] = episode.ConditionState,
                ["handlingState"] = episode.HandlingState,
                ["resource"] = new JsonObject { ["id"] = evt?.ResourceId, ["name"] = episode.ResourceName },
                ["rule"] = new JsonObject { ["id"] = evt?.RuleId, ["name"] = episode.RuleName },
                ["service"] = episode.Service,
                ["environment"] = episode.Environment,
                ["integration"] = new JsonObject { ["id"] = integration.IntegrationId.ToString(), ["name"] = integration.Name, ["type"] = integration.Type },
                ["owningTeam"] = team is null ? null : new JsonObject { ["id"] = team.TeamId.ToString(), ["name"] = team.Name },
                ["assignee"] = episode.AssigneeId?.ToString(),
                ["firstSeen"] = episode.FirstSeen.ToString("O"),
                ["lastSeen"] = episode.LastSeen.ToString("O"),
                ["occurrenceCount"] = episode.OccurrenceCount,
                ["ackDeadlineAt"] = episode.AckDeadlineAt?.ToString("O"),
                ["sourceUrl"] = episode.SourceUrl,
                ["runbookUrl"] = episode.RunbookUrl,
                ["closure"] = episode.ClosureReason is null ? null : new JsonObject
                {
                    ["reason"] = episode.ClosureReason,
                    ["evidence"] = episode.ResolutionEvidence,
                    ["at"] = episode.ClosedAt?.ToString("O"),
                    ["by"] = null,
                },
                ["coverageState"] = "unknown",
                ["groupId"] = episode.GroupId?.ToString(),
                ["routingCorrectionRequired"] = episode.RoutingCorrectionRequired,
                ["labels"] = ToJson(evt?.Labels),
                ["dimensions"] = ToJson(evt?.Dimensions),
            },
            ["escalation"] = escalation is null ? null : JsonSerializer.SerializeToNode(escalation, JsonDefaults.Stored),
            ["previous"] = previous is null ? null : JsonSerializer.SerializeToNode(previous, JsonDefaults.Stored),
        };
        return model;
    }

    /// <summary>A realistic <c>episode.opened</c> model (06 §7) used to validate templates and to preview them without a real episode.</summary>
    public static JsonObject Sample(DateTimeOffset now, string eventType = NotificationTypes.EpisodeOpened) => new()
    {
        ["event"] = eventType,
        ["deliveryId"] = "00000000-0000-0000-0000-00000000d001",
        ["sentAt"] = now.ToString("O"),
        ["episode"] = new JsonObject
        {
            ["id"] = "00000000-0000-0000-0000-00000000e001",
            ["url"] = "https://denoise.example.invalid/episodes/00000000-0000-0000-0000-00000000e001",
            ["severity"] = "critical",
            ["summary"] = "Orders API 5xx rate > 2% (sample: \"quotes\" and <tags> are escaped)",
            ["conditionState"] = "firing",
            ["handlingState"] = "new",
            ["resource"] = new JsonObject { ["id"] = "/subscriptions/…/orders-api", ["name"] = "orders-api" },
            ["rule"] = new JsonObject { ["id"] = "orders-5xx", ["name"] = "Orders 5xx rate" },
            ["service"] = "orders",
            ["environment"] = "production",
            ["integration"] = new JsonObject { ["id"] = "00000000-0000-0000-0000-00000000a001", ["name"] = "Azure Monitor Production", ["type"] = "azure_monitor" },
            ["owningTeam"] = new JsonObject { ["id"] = "00000000-0000-0000-0000-00000000c001", ["name"] = "MPT DevOps" },
            ["assignee"] = null,
            ["firstSeen"] = now.AddMinutes(-12).ToString("O"),
            ["lastSeen"] = now.ToString("O"),
            ["occurrenceCount"] = 3,
            ["ackDeadlineAt"] = now.AddMinutes(15).ToString("O"),
            ["sourceUrl"] = "https://portal.azure.com/#alerts/sample",
            ["runbookUrl"] = "https://runbooks.example.invalid/orders-5xx",
            ["closure"] = null,
            ["coverageState"] = "healthy",
            ["groupId"] = null,
            ["routingCorrectionRequired"] = false,
            ["labels"] = new JsonObject { ["team"] = "mpt" },
            ["dimensions"] = new JsonObject { ["region"] = "westeurope" },
        },
        ["escalation"] = null,
        ["previous"] = null,
    };

    /// <summary>Envelope for hub-level notifications (delivery failure) — same shape with a <c>hub</c> object instead of <c>episode</c>.</summary>
    public static JsonObject ForHub(string eventType, object detail)
        => new() { ["event"] = eventType, ["deliveryId"] = null, ["sentAt"] = null, ["hub"] = JsonSerializer.SerializeToNode(detail, JsonDefaults.Stored) };

    private static JsonNode? ToJson(IReadOnlyDictionary<string, string>? map)
    {
        if (map is null) return null;
        var obj = new JsonObject();
        foreach (var (k, v) in map) obj[k] = v;
        return obj;
    }
}
