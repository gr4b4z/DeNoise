using AlertHub.Domain.Notifications;

namespace AlertHub.Application.Notifications.Templates;

/// <summary>The four read-only templates of ADR-7 / 06 §7, seeded as version 1 and active. Administrators clone, never edit.</summary>
public static class BuiltInTemplates
{
    public static readonly Guid GenericJson = Guid.Parse("00000000-0000-0000-0000-00000000f001");
    public static readonly Guid TeamsAdaptiveCard = Guid.Parse("00000000-0000-0000-0000-00000000f002");
    public static readonly Guid SlackBlocks = Guid.Parse("00000000-0000-0000-0000-00000000f003");
    public static readonly Guid PlainText = Guid.Parse("00000000-0000-0000-0000-00000000f004");

    public static IReadOnlyList<WebhookTemplate> Create(DateTimeOffset now) =>
    [
        new()
        {
            TemplateId = GenericJson, Version = 1, Name = "generic-json", Builtin = true, Format = TemplateFormats.Json, ContentType = "application/json", CreatedBy = "system", CreatedAt = now, ActivatedAt = now,
            Description = "The full notification model (06 §7) — for receivers that parse Alert Hub's own envelope.",
            Body = "{% comment %}The full notification model is sent as-is; this body is never rendered.{% endcomment %}",
        },
        new()
        {
            TemplateId = TeamsAdaptiveCard, Version = 1, Name = "teams-adaptive-card", Builtin = true, Format = TemplateFormats.Json, ContentType = "application/json", CreatedBy = "system", CreatedAt = now, ActivatedAt = now,
            Description = "Adaptive Card 1.5 wrapped for a Power Automate Workflows HTTP trigger (Teams).",
            Body = """
            {
              "type": "message",
              "attachments": [
                {
                  "contentType": "application/vnd.microsoft.card.adaptive",
                  "contentUrl": null,
                  "content": {
                    "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                    "type": "AdaptiveCard",
                    "version": "1.5",
                    "msteams": { "width": "Full" },
                    "body": [
                      { "type": "TextBlock", "size": "Large", "weight": "Bolder", "wrap": true,
                        "color": "{% if episode.severity == 'critical' %}Attention{% elsif episode.severity == 'high' %}Warning{% else %}Default{% endif %}",
                        "text": "{{ episode.severity | upcase }} · {{ episode.summary }}" },
                      { "type": "TextBlock", "isSubtle": true, "wrap": true, "spacing": "None",
                        "text": "{{ event }}{% if escalation %} · {{ escalation.reason }}{% endif %}{% if previous %} · was {{ previous.severity }}{% endif %}" },
                      { "type": "FactSet", "facts": [
                        { "title": "Resource", "value": "{{ episode.resource.name }}" },
                        { "title": "Service", "value": "{{ episode.service }} ({{ episode.environment }})" },
                        { "title": "Owner", "value": "{{ episode.owningTeam.name }}" },
                        { "title": "Condition", "value": "{{ episode.conditionState }} / {{ episode.handlingState }}" },
                        { "title": "Seen", "value": "{{ episode.occurrenceCount }}× since {{ episode.firstSeen }}" }{% if episode.closure %},
                        { "title": "Closure", "value": "{{ episode.closure.reason }} ({{ episode.closure.evidence }})" }{% endif %}
                      ] }
                    ],
                    "actions": [
                      { "type": "Action.OpenUrl", "title": "Open in Alert Hub", "url": "{{ episode.url }}" }{% if episode.runbookUrl %},
                      { "type": "Action.OpenUrl", "title": "Runbook", "url": "{{ episode.runbookUrl }}" }{% endif %}{% if episode.sourceUrl %},
                      { "type": "Action.OpenUrl", "title": "Source", "url": "{{ episode.sourceUrl }}" }{% endif %}
                    ]
                  }
                }
              ]
            }
            """,
        },
        new()
        {
            TemplateId = SlackBlocks, Version = 1, Name = "slack-blocks", Builtin = true, Format = TemplateFormats.Json, ContentType = "application/json", CreatedBy = "system", CreatedAt = now, ActivatedAt = now,
            Description = "Slack Block Kit message for an incoming webhook.",
            Body = """
            {
              "text": "[{{ episode.severity | upcase }}] {{ episode.summary }}",
              "blocks": [
                { "type": "header", "text": { "type": "plain_text", "text": "{{ episode.severity | upcase }} · {{ episode.summary | truncate: 120 }}", "emoji": false } },
                { "type": "section", "fields": [
                  { "type": "mrkdwn", "text": "*Resource*\n{{ episode.resource.name }}" },
                  { "type": "mrkdwn", "text": "*Service*\n{{ episode.service }} ({{ episode.environment }})" },
                  { "type": "mrkdwn", "text": "*Owner*\n{{ episode.owningTeam.name }}" },
                  { "type": "mrkdwn", "text": "*State*\n{{ episode.conditionState }} / {{ episode.handlingState }}" }
                ] },
                { "type": "context", "elements": [ { "type": "mrkdwn", "text": "{{ event }} · seen {{ episode.occurrenceCount }}× · delivery {{ deliveryId }}" } ] },
                { "type": "actions", "elements": [
                  { "type": "button", "text": { "type": "plain_text", "text": "Open in Alert Hub" }, "url": "{{ episode.url }}" }
                ] }
              ]
            }
            """,
        },
        new()
        {
            TemplateId = PlainText, Version = 1, Name = "plain-text", Builtin = true, Format = TemplateFormats.Text, ContentType = "text/plain; charset=utf-8", CreatedBy = "system", CreatedAt = now, ActivatedAt = now,
            Description = "Plain text — ticketing systems, pagers, logs.",
            Body = """
            [AlertHub][{{ episode.severity }}] {{ episode.summary }}
            Event: {{ event }}{% if escalation %} ({{ escalation.reason }}){% endif %}
            Resource: {{ episode.resource.name }} · Service: {{ episode.service }} · Environment: {{ episode.environment }}
            Owner: {{ episode.owningTeam.name }} · Condition: {{ episode.conditionState }} · Handling: {{ episode.handlingState }}
            Seen {{ episode.occurrenceCount }} time(s) since {{ episode.firstSeen }}{% if episode.closure %}
            Closed: {{ episode.closure.reason }} (evidence {{ episode.closure.evidence }}){% endif %}
            {{ episode.url }}
            """,
        },
    ];
}
