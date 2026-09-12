using System.Text.Json.Nodes;
using DeNoise.Domain.Integrations;

namespace DeNoise.Application.Mapping;

/// <summary>One mapping of a reference set: seeded as version 1 (active) when an integration of the type is created.</summary>
public sealed record ReferenceMapping(string Name, int Order, string Yaml, IReadOnlyList<MappingSample> Samples);

/// <summary>
/// Reference mappings of 07 §2–3, shipped as seeded versions so an Azure Monitor or Atlas integration interprets events
/// before anyone edits anything. Tenant-specific parts (environment by subscription / project, Atlas severity per alert
/// configuration) are left at <c>default: unknown</c> and are the operator's first edit in the wizard (owner input C5).
/// Payload samples are Microsoft's / MongoDB's published shapes and count as <c>PROVISIONAL-*</c> until real captures exist.
/// </summary>
public static class ReferenceMappings
{
    public const string AzureCanaryRulePrefix = "denoise-canary-";

    /// <summary>Azure Monitor Common Alert Schema — the 07 §1 example without tenant lookups (order 100: evaluated last).</summary>
    public const string AzureMonitorYaml = """
        mapping:
          identity_version: 1
          ignore_paths_for_body_key: ["$.data.essentials.firedDateTime"]
          fields:
            event_type:
              lookup:
                from: "$.data.essentials.monitorCondition"
                map: { Fired: firing, Resolved: resolved }
                default: unknown
            source_alert_id:  { path: "$.data.essentials.alertId" }
            source_event_id:  { first_nonempty: [ { path: "$.data.essentials.originAlertId" }, { path: "$.id" } ] }
            occurred_at:
              first_nonempty:
                - { path: "$.data.essentials.resolvedDateTime", when: { eq: [ "$.data.essentials.monitorCondition", "Resolved" ] } }
                - { path: "$.data.essentials.firedDateTime" }
              as: timestamp
            severity:
              lookup: { from: "$.data.essentials.severity", map: { Sev0: critical, Sev1: high, Sev2: medium, Sev3: low, Sev4: informational }, default: unknown }
            summary:          { template: "{$.data.essentials.alertRule}: {$.data.essentials.description}", max_length: 500 }
            resource_id:      { path: "$.data.essentials.alertTargetIDs[0]", as: string, transform: [lower] }
            resource_name:    { path: "$.data.essentials.configurationItems[0]" }
            rule_id:          { path: "$.data.essentials.alertRuleID", transform: [lower] }
            rule_name:        { path: "$.data.essentials.alertRule" }
            # Fill regex_map with your subscription ids (07 §2): "/subscriptions/<prod-sub>.*": production
            environment:      { lookup: { from: "$.data.essentials.alertTargetIDs[0]", regex_map: {}, default: unknown } }
            service:          { path: "$.data.customProperties.service", default: null }
            source_url:       { path: "$.data.essentials.investigationLink" }
            dimensions:       { object: { metric: { path: "$.data.alertContext.condition.allOf[0].metricName" }, ns: { path: "$.data.alertContext.condition.allOf[0].metricNamespace" } } }
            labels:           { pick: [ "$.data.essentials.monitoringService", "$.data.essentials.signalType" ], as: object }
          required: [event_type, source_alert_id, rule_id]
          identity:
            - { name: scope,       path: "$.data.essentials.alertTargetIDs[0]", transform: [lower, azure_subscription] }
            - { name: environment, field: environment }
            - { name: resource,    field: resource_id }
            - { name: rule,        field: rule_id }
            - { name: dims,        field: dimensions, optional: true }
          lifecycle_profile_hint: explicit_recovery
        """;

    /// <summary>Managed canary rules (<c>denoise-canary-*</c>, spec §13.4) become coverage signals, never episodes (order 10).</summary>
    public const string AzureCanaryYaml = """
        mapping:
          identity_version: 1
          applies_when: { regex: [ "$.data.essentials.alertRule", "^denoise-canary-" ] }
          fields:
            event_type:       { const: heartbeat }
            source_alert_id:  { path: "$.data.essentials.alertId" }
            source_event_id:  { first_nonempty: [ { path: "$.data.essentials.originAlertId" }, { path: "$.id" } ] }
            occurred_at:      { path: "$.data.essentials.firedDateTime", as: timestamp }
            severity:         { const: informational }
            summary:          { template: "canary {$.data.essentials.alertRule}" }
            rule_id:          { path: "$.data.essentials.alertRuleID", transform: [lower] }
            rule_name:        { path: "$.data.essentials.alertRule" }
            resource_id:      { path: "$.data.essentials.alertTargetIDs[0]", as: string, transform: [lower] }
            labels:           { object: { canary: { path: "$.data.essentials.monitorCondition", default: ok } } }
          required: [event_type, source_alert_id, rule_id]
          identity:
            - { name: scope, path: "$.data.essentials.alertTargetIDs[0]", transform: [lower, azure_subscription] }
            - { name: rule,  field: rule_id }
          lifecycle_profile_hint: one_shot
        """;

    /// <summary>Resource Health: <c>Available</c> is a recovery, everything else a firing condition (07 §2 table, order 50).</summary>
    public const string AzureResourceHealthYaml = """
        mapping:
          identity_version: 1
          applies_when: { eq: [ "$.data.essentials.monitoringService", "Resource Health" ] }
          fields:
            event_type:
              lookup: { from: "$.data.alertContext.properties.currentHealthStatus", map: { Available: resolved, Unavailable: firing, Degraded: firing, Unknown: firing }, default: firing }
            source_alert_id:  { path: "$.data.essentials.alertId" }
            source_event_id:  { first_nonempty: [ { path: "$.data.essentials.originAlertId" }, { path: "$.id" } ] }
            occurred_at:      { path: "$.data.essentials.firedDateTime", as: timestamp }
            severity:
              lookup: { from: "$.data.essentials.severity", map: { Sev0: critical, Sev1: high, Sev2: medium, Sev3: low, Sev4: informational }, default: unknown }
            summary:          { template: "{$.data.essentials.alertRule}: {$.data.alertContext.properties.currentHealthStatus} ({$.data.alertContext.properties.cause})", max_length: 500 }
            resource_id:      { path: "$.data.essentials.alertTargetIDs[0]", as: string, transform: [lower] }
            resource_name:    { path: "$.data.essentials.configurationItems[0]" }
            rule_id:          { path: "$.data.essentials.alertRuleID", transform: [lower] }
            rule_name:        { path: "$.data.essentials.alertRule" }
            environment:      { lookup: { from: "$.data.essentials.alertTargetIDs[0]", regex_map: {}, default: unknown } }
            source_url:       { path: "$.data.essentials.investigationLink" }
            dimensions:       { object: { status: { path: "$.data.alertContext.properties.currentHealthStatus" } } }
            labels:           { pick: [ "$.data.essentials.monitoringService", "$.data.essentials.signalType" ], as: object }
          required: [event_type, source_alert_id, rule_id]
          identity:
            - { name: scope,       path: "$.data.essentials.alertTargetIDs[0]", transform: [lower, azure_subscription] }
            - { name: environment, field: environment }
            - { name: resource,    field: resource_id }
            - { name: rule,        field: rule_id }
          lifecycle_profile_hint: explicit_recovery
        """;

    /// <summary>Service Health: one-shot informational notices (07 §2 table, order 60).</summary>
    public const string AzureServiceHealthYaml = """
        mapping:
          identity_version: 1
          applies_when: { eq: [ "$.data.essentials.monitoringService", "ServiceHealth" ] }
          fields:
            event_type:       { const: informational }
            source_alert_id:  { path: "$.data.essentials.alertId" }
            source_event_id:  { first_nonempty: [ { path: "$.data.essentials.originAlertId" }, { path: "$.id" } ] }
            occurred_at:      { path: "$.data.essentials.firedDateTime", as: timestamp }
            severity:         { const: informational }
            summary:          { template: "{$.data.essentials.alertRule}: {$.data.alertContext.properties.title}", max_length: 500 }
            resource_id:      { path: "$.data.essentials.alertTargetIDs[0]", as: string, transform: [lower] }
            rule_id:          { path: "$.data.essentials.alertRuleID", transform: [lower] }
            rule_name:        { path: "$.data.essentials.alertRule" }
            source_url:       { path: "$.data.essentials.investigationLink" }
            labels:           { pick: [ "$.data.essentials.monitoringService", "$.data.alertContext.properties.incidentType" ], as: object }
          required: [event_type, source_alert_id, rule_id]
          identity:
            - { name: scope, path: "$.data.essentials.alertTargetIDs[0]", transform: [lower, azure_subscription] }
            - { name: rule,  field: rule_id }
            - { name: event, path: "$.data.alertContext.properties.trackingId", optional: true }
          lifecycle_profile_hint: one_shot
        """;

    /// <summary>MongoDB Atlas alert webhook (07 §3). Severity and environment maps are the operator's first edit.</summary>
    public const string AtlasYaml = """
        mapping:
          identity_version: 1
          fields:
            event_type:
              first_nonempty:
                - { lookup: { from: "$.status", map: { OPEN: firing, TRACKING: update, CLOSED: resolved, CANCELLED: cancelled } } }
                - { lookup: { from: "$headers['X-MMS-Event']", map: { alert.open: firing, alert.update: update, alert.close: resolved, alert.cancel: cancelled }, default: unknown } }
            source_alert_id:  { path: "$.id" }
            source_event_id:  { template: "{$.id}:{$.status}:{$.updated}" }
            occurred_at:      { first_nonempty: [ { path: "$.resolved" }, { path: "$.updated" }, { path: "$.created" } ], as: timestamp }
            # Severity is not in the payload: map your project's alert configurations here (07 §3, owner input C5).
            severity:
              lookup: { from: "$.eventTypeName", map: { HOST_DOWN: critical, NO_PRIMARY: critical, REPLICATION_OPLOG_WINDOW_RUNNING_OUT: high, OUTSIDE_METRIC_THRESHOLD: medium, CREDIT_CARD_ABOUT_TO_EXPIRE: informational }, default: unknown }
            summary:          { template: "{$.eventTypeName} on {$.clusterName} {$.hostnameAndPort}", max_length: 500 }
            resource_id:      { first_nonempty: [ { path: "$.hostnameAndPort", transform: [lower] }, { path: "$.clusterName", transform: [lower] } ] }
            resource_name:    { first_nonempty: [ { path: "$.clusterName" }, { path: "$.hostnameAndPort" } ] }
            rule_id:          { first_nonempty: [ { path: "$.alertConfigId" }, { path: "$.eventTypeName" } ] }
            rule_name:        { path: "$.eventTypeName" }
            # Map Atlas project (group) ids to environments: "<prodProjectId>": production
            environment:      { lookup: { from: "$.groupId", map: {}, default: unknown } }
            dimensions:       { object: { metric: { path: "$.metricName" }, replicaSet: { path: "$.replicaSetName" } } }
            labels:           { object: { mmsEvent: { header: "X-MMS-Event" }, project: { path: "$.groupId" } } }
            source_url:       { path: "$.links[?(@.rel=='http://mms.mongodb.com/alert')].href" }
          required: [event_type, source_alert_id, rule_id]
          identity:
            - { name: project,     path: "$.groupId" }
            - { name: environment, field: environment }
            - { name: resource,    field: resource_id }
            - { name: rule,        field: rule_id }
            - { name: dims,        field: dimensions, optional: true }
          lifecycle_profile_hint: queryable_state
        """;

    public const string AzureFiredSample = """
        { "schemaId": "azureMonitorCommonAlertSchema", "id": "PROVISIONAL-fired-1",
          "data": { "essentials": {
              "alertId": "/subscriptions/00000000-0000-0000-0000-000000000001/providers/Microsoft.AlertsManagement/alerts/11111111-1111-1111-1111-111111111111",
              "alertRule": "cpu-high", "alertRuleID": "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-prod/providers/Microsoft.Insights/metricAlerts/cpu-high",
              "severity": "Sev1", "signalType": "Metric", "monitorCondition": "Fired", "monitoringService": "Platform",
              "alertTargetIDs": ["/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-prod/providers/Microsoft.Compute/virtualMachines/vm1"],
              "configurationItems": ["vm1"], "originAlertId": "PROVISIONAL-origin-1",
              "firedDateTime": "2026-09-11T10:00:00Z", "resolvedDateTime": null, "description": "CPU above 90%",
              "essentialsVersion": "1.0", "alertContextVersion": "1.0", "investigationLink": "https://portal.azure.com/#view/alert/1" },
            "alertContext": { "condition": { "allOf": [ { "metricName": "Percentage CPU", "metricNamespace": "Microsoft.Compute/virtualMachines", "dimensions": [] } ] } },
            "customProperties": {} } }
        """;

    public const string AzureResolvedSample = """
        { "schemaId": "azureMonitorCommonAlertSchema", "id": "PROVISIONAL-resolved-1",
          "data": { "essentials": {
              "alertId": "/subscriptions/00000000-0000-0000-0000-000000000001/providers/Microsoft.AlertsManagement/alerts/11111111-1111-1111-1111-111111111111",
              "alertRule": "cpu-high", "alertRuleID": "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-prod/providers/Microsoft.Insights/metricAlerts/cpu-high",
              "severity": "Sev1", "signalType": "Metric", "monitorCondition": "Resolved", "monitoringService": "Platform",
              "alertTargetIDs": ["/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-prod/providers/Microsoft.Compute/virtualMachines/vm1"],
              "configurationItems": ["vm1"], "originAlertId": "PROVISIONAL-origin-2",
              "firedDateTime": "2026-09-11T10:00:00Z", "resolvedDateTime": "2026-09-11T10:20:00Z", "description": "CPU above 90%",
              "essentialsVersion": "1.0", "alertContextVersion": "1.0", "investigationLink": "https://portal.azure.com/#view/alert/1" },
            "alertContext": { "condition": { "allOf": [ { "metricName": "Percentage CPU", "metricNamespace": "Microsoft.Compute/virtualMachines", "dimensions": [] } ] } },
            "customProperties": {} } }
        """;

    public const string AzureCanarySample = """
        { "schemaId": "azureMonitorCommonAlertSchema", "id": "PROVISIONAL-canary-1",
          "data": { "essentials": {
              "alertId": "/subscriptions/00000000-0000-0000-0000-000000000001/providers/Microsoft.AlertsManagement/alerts/22222222-2222-2222-2222-222222222222",
              "alertRule": "denoise-canary-production", "alertRuleID": "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-mon/providers/Microsoft.Insights/scheduledQueryRules/denoise-canary-production",
              "severity": "Sev4", "signalType": "Log", "monitorCondition": "Fired", "monitoringService": "Log Alerts V2",
              "alertTargetIDs": ["/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-mon/providers/Microsoft.OperationalInsights/workspaces/law-prod"],
              "configurationItems": ["law-prod"], "originAlertId": "PROVISIONAL-canary-origin-1",
              "firedDateTime": "2026-09-11T10:05:00Z", "resolvedDateTime": null, "description": "always fires",
              "essentialsVersion": "1.0", "alertContextVersion": "1.1", "investigationLink": "https://portal.azure.com/#view/alert/2" },
            "alertContext": { "condition": { "allOf": [ { "searchQuery": "print 1", "dimensions": [] } ] } },
            "customProperties": {} } }
        """;

    public const string AtlasOpenSample = """
        { "id": "PROVISIONAL-atlas-1", "groupId": "5f1234567890abcdef123456", "alertConfigId": "5f1234567890abcdef654321",
          "eventTypeName": "OUTSIDE_METRIC_THRESHOLD", "status": "OPEN", "created": "2026-09-11T10:00:00Z", "updated": "2026-09-11T10:00:00Z",
          "clusterName": "orders-prod", "hostnameAndPort": "orders-prod-shard-00-01.abcde.mongodb.net:27017", "replicaSetName": "atlas-abcdef-shard-0",
          "metricName": "CONNECTIONS_PERCENT", "currentValue": { "number": 92.5, "units": "RAW" },
          "links": [ { "rel": "http://mms.mongodb.com/alert", "href": "https://cloud.mongodb.com/v2/5f1234567890abcdef123456#/alerts/PROVISIONAL-atlas-1" } ] }
        """;

    public static IReadOnlyList<ReferenceMapping> For(string integrationType) => integrationType switch
    {
        IntegrationTypes.AzureMonitor =>
        [
            new ReferenceMapping("azure-canary", 10, AzureCanaryYaml, [Sample("PROVISIONAL-canary", AzureCanarySample, null, ("event_type", "heartbeat"), ("severity", "informational"))]),
            new ReferenceMapping("azure-resource-health", 50, AzureResourceHealthYaml, []),
            new ReferenceMapping("azure-service-health", 60, AzureServiceHealthYaml, []),
            new ReferenceMapping("azure-common-alert-schema", 100, AzureMonitorYaml,
            [
                Sample("PROVISIONAL-fired", AzureFiredSample, null, ("event_type", "firing"), ("severity", "high"), ("resource_name", "vm1")),
                Sample("PROVISIONAL-resolved", AzureResolvedSample, null, ("event_type", "resolved"), ("rule_name", "cpu-high")),
            ]),
        ],
        IntegrationTypes.Atlas =>
        [
            new ReferenceMapping("atlas-webhook", 100, AtlasYaml,
            [
                Sample("PROVISIONAL-open", AtlasOpenSample, new Dictionary<string, string> { ["X-MMS-Event"] = "alert.open" }, ("event_type", "firing"), ("severity", "medium"), ("resource_name", "orders-prod")),
            ]),
        ],
        _ => [],
    };

    private static MappingSample Sample(string name, string json, IReadOnlyDictionary<string, string>? headers, params (string Field, string Expected)[] expected)
        => new(name, JsonNode.Parse(json)!, headers, expected.ToDictionary(e => e.Field, e => e.Expected, StringComparer.Ordinal));
}
