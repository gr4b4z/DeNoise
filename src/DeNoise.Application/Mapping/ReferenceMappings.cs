using System.Text.Json.Nodes;
using DeNoise.Domain.Integrations;

namespace DeNoise.Application.Mapping;

/// <summary>One mapping of a reference set: seeded as version 1 (active) when an integration of the type is created.</summary>
public sealed record ReferenceMapping(string Name, int Order, string Yaml, IReadOnlyList<MappingSample> Samples);

/// <summary>
/// Reference mappings of 07 §2–3 and the milestone 16 pipeline sources (Azure DevOps Service Hooks, GitHub webhooks), shipped as
/// seeded versions so an integration interprets events before anyone edits anything. Tenant-specific parts (environment by subscription / project, Atlas severity per alert
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

    /// <summary>Azure DevOps <c>build.complete</c> (classic and YAML single-stage pipelines): one condition per pipeline and, for builds, per branch (M16).</summary>
    public const string AzureDevopsBuildYaml = """
        mapping:
          identity_version: 1
          applies_when: { eq: [ "$.eventType", build.complete ] }
          fields:
            labels:
              object:
                event:    { path: "$.eventType" }
                project:  { path: "$.resource.project.name" }
                pipeline: { path: "$.resource.definition.name" }
                kind:
                  lookup:
                    from: "$.resource.definition.name"
                    # Assumed naming convention until the real names arrive (M16): *-ci / ci-* / build-* → build, deploy-* / *-cd(-env) → deploy, promote-* / *-promotion → promote, else build.
                    regex_map: { "(?i)(ci|build)-.*": build, "(?i).*-ci": build, "(?i)deploy-.*": deploy, "(?i).*-cd(-.*)?": deploy, "(?i)promote-.*": promote, "(?i).*-promotion": promote }
                    default: build
                result:   { first_nonempty: [ { path: "$.resource.result" }, { path: "$.resource.status" } ] }
                reason:   { path: "$.resource.reason" }
                branch:   { first_nonempty: [ { path: "$.resource.sourceBranch" }, { path: "$.resource.sourceGetVersion" } ] }
                run:      { path: "$.resource.buildNumber" }
                commit:   { path: "$.resource.sourceVersion", transform: ["truncate:12"] }
                actor:    { path: "$.resource.requestedFor.displayName" }
            event_type:
              lookup: { from: labels.result, map: { succeeded: resolved, failed: firing, partiallySucceeded: firing }, default: informational }
            source_alert_id:  { path: "$.resource.id", as: string }
            source_event_id:  { path: "$.id" }
            occurred_at:      { first_nonempty: [ { path: "$.resource.finishTime" }, { path: "$.createdDate" } ], as: timestamp }
            # Builds are always `ci` (M16 decision 2); deploy and promote pipelines name their target.
            environment:
              first_nonempty:
                - { const: ci, when: { eq: [labels.kind, build] } }
                - lookup:
                    from: "$.resource.definition.name"
                    regex_map:
                      "(?i).*(-cd-|deploy-|promote-)(prod|production)(-.*)?": production
                      "(?i).*(-cd-|deploy-|promote-)(stg|stage|staging|uat)(-.*)?": staging
                      "(?i).*(-cd-|deploy-|promote-)(dev|development)(-.*)?": development
                      "(?i).*(-cd-|deploy-|promote-)(pr|eph|preview)-.*": ephemeral
                    default: unknown
            severity:
              first_nonempty:
                - { const: informational, when: { eq: [event_type, informational] } }
                - { lookup: { from: environment, map: { production: high, staging: medium, development: low, ephemeral: low, unknown: medium } }, when: { neq: [labels.kind, build] } }
                - { const: informational, when: { eq: [labels.reason, pullRequest] } }
                - { lookup: { from: labels.branch, regex_map: { "(LG:)?refs/heads/(main|master)(:.*)?": high, "(LG:)?refs/heads/(release|hotfix)/.*": high, "refs/pull/.*": informational }, default: low } }
            # A run that neither failed nor succeeded (cancelled, skipped, still running) has no rule: it is recorded, but there is no condition and therefore no episode (04 §2.1).
            rule_id:   { lookup: { from: labels.kind, map: { build: build-failed, deploy: deploy-failed, promote: promotion-failed } }, when: { in: [event_type, [firing, resolved]] } }
            rule_name: { lookup: { from: labels.kind, map: { build: Build failed, deploy: Deployment failed, promote: Promotion failed } } }
            resource_id:   { template: "{$.resource.project.name}/{$.resource.definition.name}" }
            resource_name: { path: "$.resource.definition.name" }
            summary:       { template: "{labels.kind} {labels.pipeline} #{labels.run} {labels.result} ({labels.branch})", max_length: 500 }
            source_url:    { first_nonempty: [ { path: "$.resource._links.web.href" }, { path: "$.resource.url" } ] }
            dimensions:
              object:
                branch: { template: "{labels.branch}", when: { eq: [labels.kind, build] } }
          required: [event_type, source_alert_id]
          identity:
            - { name: project,     path: "$.resourceContainers.project.id" }
            - { name: environment, field: environment }
            - { name: pipeline,    path: "$.resource.definition.id" }
            - { name: rule,        field: rule_id }
            - { name: dims,        field: dimensions, optional: true }
          lifecycle_profile_hint: explicit_recovery
        """;

    /// <summary>Azure DevOps <c>ms.vss-pipelines.run-state-changed-event</c>: whole-run result of a YAML pipeline; subscribe with state <c>completed</c> only.</summary>
    public const string AzureDevopsRunYaml = """
        mapping:
          identity_version: 1
          applies_when: { eq: [ "$.eventType", ms.vss-pipelines.run-state-changed-event ] }
          fields:
            labels:
              object:
                event:    { path: "$.eventType" }
                pipeline: { path: "$.resource.pipeline.name" }
                kind:
                  lookup:
                    from: "$.resource.pipeline.name"
                    # Assumed naming convention until the real names arrive (M16): *-ci / ci-* / build-* → build, deploy-* / *-cd(-env) → deploy, promote-* / *-promotion → promote, else build.
                    regex_map: { "(?i)(ci|build)-.*": build, "(?i).*-ci": build, "(?i)deploy-.*": deploy, "(?i).*-cd(-.*)?": deploy, "(?i)promote-.*": promote, "(?i).*-promotion": promote }
                    default: build
                state:    { path: "$.resource.run.state" }
                result:   { path: "$.resource.run.result" }
                branch:   { path: "$.resource.run.resources.repositories.self.refName" }
                run:      { path: "$.resource.run.name" }
                commit:   { path: "$.resource.run.resources.repositories.self.version", transform: ["truncate:12"] }
            event_type:
              lookup: { from: labels.result, map: { succeeded: resolved, failed: firing, partiallySucceeded: firing }, default: informational }
            source_alert_id:  { path: "$.resource.run.id", as: string }
            source_event_id:  { path: "$.id" }
            occurred_at:      { first_nonempty: [ { path: "$.resource.run.finishedDate" }, { path: "$.createdDate" } ], as: timestamp }
            environment:
              first_nonempty:
                - { const: ci, when: { eq: [labels.kind, build] } }
                - lookup:
                    from: "$.resource.pipeline.name"
                    regex_map:
                      "(?i).*(-cd-|deploy-|promote-)(prod|production)(-.*)?": production
                      "(?i).*(-cd-|deploy-|promote-)(stg|stage|staging|uat)(-.*)?": staging
                      "(?i).*(-cd-|deploy-|promote-)(dev|development)(-.*)?": development
                      "(?i).*(-cd-|deploy-|promote-)(pr|eph|preview)-.*": ephemeral
                    default: unknown
            severity:
              first_nonempty:
                - { const: informational, when: { eq: [event_type, informational] } }
                - { lookup: { from: environment, map: { production: high, staging: medium, development: low, ephemeral: low, unknown: medium } }, when: { neq: [labels.kind, build] } }
                - { lookup: { from: labels.branch, regex_map: { "(LG:)?refs/heads/(main|master)(:.*)?": high, "(LG:)?refs/heads/(release|hotfix)/.*": high, "refs/pull/.*": informational }, default: low } }
            # A run that neither failed nor succeeded (cancelled, skipped, still running) has no rule: it is recorded, but there is no condition and therefore no episode (04 §2.1).
            rule_id:   { lookup: { from: labels.kind, map: { build: build-failed, deploy: deploy-failed, promote: promotion-failed } }, when: { in: [event_type, [firing, resolved]] } }
            rule_name: { lookup: { from: labels.kind, map: { build: Build failed, deploy: Deployment failed, promote: Promotion failed } } }
            resource_id:   { template: "{$.resourceContainers.project.id}/{$.resource.pipeline.name}" }
            resource_name: { path: "$.resource.pipeline.name" }
            summary:       { template: "{labels.kind} {labels.pipeline} #{labels.run} {labels.result}", max_length: 500 }
            source_url:    { first_nonempty: [ { path: "$.resource.run._links.web.href" }, { path: "$.resource.runUrl" } ] }
            dimensions:
              object:
                branch: { template: "{labels.branch}", when: { eq: [labels.kind, build] } }
          required: [event_type, source_alert_id]
          identity:
            - { name: project,     path: "$.resourceContainers.project.id" }
            - { name: environment, field: environment }
            - { name: pipeline,    path: "$.resource.pipeline.id" }
            - { name: rule,        field: rule_id }
            - { name: dims,        field: dimensions, optional: true }
          lifecycle_profile_hint: explicit_recovery
        """;

    /// <summary>Azure DevOps <c>ms.vss-pipelines.stage-state-changed-event</c>: one condition per stage; a stage named after its environment is a deployment target.</summary>
    public const string AzureDevopsStageYaml = """
        mapping:
          identity_version: 1
          applies_when: { eq: [ "$.eventType", ms.vss-pipelines.stage-state-changed-event ] }
          fields:
            labels:
              object:
                event:    { path: "$.eventType" }
                pipeline: { path: "$.resource.pipeline.name" }
                kind:
                  lookup:
                    from: "$.resource.pipeline.name"
                    # Assumed naming convention until the real names arrive (M16): *-ci / ci-* / build-* → build, deploy-* / *-cd(-env) → deploy, promote-* / *-promotion → promote, else build.
                    regex_map: { "(?i)(ci|build)-.*": build, "(?i).*-ci": build, "(?i)deploy-.*": deploy, "(?i).*-cd(-.*)?": deploy, "(?i)promote-.*": promote, "(?i).*-promotion": promote }
                    default: build
                stage:    { path: "$.resource.stage.name" }
                result:   { path: "$.resource.stage.result" }
                branch:   { path: "$.resource.run.resources.repositories.self.refName" }
                run:      { path: "$.resource.run.name" }
            event_type:
              lookup: { from: labels.result, map: { succeeded: resolved, failed: firing, partiallySucceeded: firing }, default: informational }
            source_alert_id:  { template: "{$.resource.run.id}:{$.resource.stage.name}" }
            source_event_id:  { path: "$.id" }
            occurred_at:      { path: "$.createdDate", as: timestamp }
            # A stage named after its environment (production, staging, dev, pr-123) wins; otherwise the pipeline name decides.
            environment:
              first_nonempty:
                - { const: ci, when: { eq: [labels.kind, build] } }
                - lookup:
                    from: "$.resource.stage.name"
                    regex_map:
                      "(?i)(.*[-_ ])?(prod|production)([-_ ].*)?": production
                      "(?i)(.*[-_ ])?(stg|stage|staging|uat)([-_ ].*)?": staging
                      "(?i)(.*[-_ ])?(dev|development)([-_ ].*)?": development
                      "(?i)(.*[-_ ])?(pr|eph|preview)-.*": ephemeral
                - lookup:
                    from: "$.resource.pipeline.name"
                    regex_map:
                      "(?i).*(-cd-|deploy-|promote-)(prod|production)(-.*)?": production
                      "(?i).*(-cd-|deploy-|promote-)(stg|stage|staging|uat)(-.*)?": staging
                      "(?i).*(-cd-|deploy-|promote-)(dev|development)(-.*)?": development
                      "(?i).*(-cd-|deploy-|promote-)(pr|eph|preview)-.*": ephemeral
                    default: unknown
            severity:
              first_nonempty:
                - { const: informational, when: { eq: [event_type, informational] } }
                - { lookup: { from: environment, map: { production: high, staging: medium, development: low, ephemeral: low, unknown: medium } }, when: { neq: [labels.kind, build] } }
                - { lookup: { from: labels.branch, regex_map: { "(LG:)?refs/heads/(main|master)(:.*)?": high, "(LG:)?refs/heads/(release|hotfix)/.*": high, "refs/pull/.*": informational }, default: low } }
            # A run that neither failed nor succeeded (cancelled, skipped, still running) has no rule: it is recorded, but there is no condition and therefore no episode (04 §2.1).
            rule_id:   { lookup: { from: labels.kind, map: { build: build-failed, deploy: deploy-failed, promote: promotion-failed } }, when: { in: [event_type, [firing, resolved]] } }
            rule_name: { lookup: { from: labels.kind, map: { build: Build failed, deploy: Deployment failed, promote: Promotion failed } } }
            resource_id:   { template: "{$.resourceContainers.project.id}/{$.resource.pipeline.name}" }
            resource_name: { path: "$.resource.pipeline.name" }
            summary:       { template: "{labels.kind} {labels.pipeline} #{labels.run} stage {labels.stage} {labels.result}", max_length: 500 }
            source_url:    { first_nonempty: [ { path: "$.resource.stage._links.web.href" }, { path: "$.resource.run._links.web.href" }, { path: "$.resource.runUrl" } ] }
            dimensions:
              object:
                branch: { template: "{labels.branch}", when: { eq: [labels.kind, build] } }
                stage:  { path: "$.resource.stage.name" }
          required: [event_type, source_alert_id]
          identity:
            - { name: project,     path: "$.resourceContainers.project.id" }
            - { name: environment, field: environment }
            - { name: pipeline,    path: "$.resource.pipeline.id" }
            - { name: rule,        field: rule_id }
            - { name: dims,        field: dimensions }
          lifecycle_profile_hint: explicit_recovery
        """;

    /// <summary>Azure DevOps classic release <c>ms.vss-release.deployment-completed-event</c>: one condition per release definition and stage.</summary>
    public const string AzureDevopsReleaseYaml = """
        mapping:
          identity_version: 1
          applies_when: { eq: [ "$.eventType", ms.vss-release.deployment-completed-event ] }
          fields:
            labels:
              object:
                event:    { path: "$.eventType" }
                pipeline: { path: "$.resource.environment.releaseDefinition.name" }
                kind:     { const: deploy }
                stage:    { path: "$.resource.environment.name" }
                result:   { first_nonempty: [ { path: "$.resource.deployment.deploymentStatus" }, { path: "$.resource.environment.status" } ] }
                release:  { path: "$.resource.environment.release.name" }
                attempt:  { path: "$.resource.deployment.attempt" }
                actor:    { path: "$.resource.environment.releaseCreatedBy.displayName" }
            event_type:
              lookup: { from: labels.result, map: { succeeded: resolved, failed: firing, partiallySucceeded: firing, rejected: firing }, default: informational }
            source_alert_id:  { first_nonempty: [ { path: "$.resource.deployment.id" }, { template: "{$.resource.environment.releaseId}:{$.resource.environment.id}" } ], as: string }
            source_event_id:  { path: "$.id" }
            occurred_at:      { first_nonempty: [ { path: "$.resource.deployment.completedOn" }, { path: "$.resource.environment.modifiedOn" }, { path: "$.createdDate" } ], as: timestamp }
            environment:
              first_nonempty:
                - lookup:
                    from: "$.resource.environment.name"
                    regex_map:
                      "(?i)(.*[-_ ])?(prod|production)([-_ ].*)?": production
                      "(?i)(.*[-_ ])?(stg|stage|staging|uat)([-_ ].*)?": staging
                      "(?i)(.*[-_ ])?(dev|development)([-_ ].*)?": development
                      "(?i)(.*[-_ ])?(pr|eph|preview)-.*": ephemeral
                - { const: unknown }
            severity:
              first_nonempty:
                - { const: informational, when: { eq: [event_type, informational] } }
                - { lookup: { from: environment, map: { production: high, staging: medium, development: low, ephemeral: low }, default: medium } }
            rule_id:   { const: deploy-failed, when: { in: [event_type, [firing, resolved]] } }
            rule_name: { const: Deployment failed }
            resource_id:   { template: "{$.resourceContainers.project.id}/{$.resource.environment.releaseDefinition.name}" }
            resource_name: { path: "$.resource.environment.releaseDefinition.name" }
            summary:       { template: "deploy {labels.pipeline} {labels.release} to {labels.stage} {labels.result}", max_length: 500 }
            source_url:    { first_nonempty: [ { path: "$.resource.environment.release._links.web.href" }, { path: "$.resource.url" } ] }
          required: [event_type, source_alert_id]
          identity:
            - { name: project,     path: "$.resourceContainers.project.id" }
            - { name: environment, field: environment }
            - { name: pipeline,    path: "$.resource.environment.releaseDefinition.id" }
            - { name: target,      path: "$.resource.environment.name", transform: [lower] }
            - { name: rule,        field: rule_id }
          lifecycle_profile_hint: explicit_recovery
        """;

    /// <summary>GitHub <c>workflow_run</c>: whole-workflow result; requested / in_progress deliveries are informational and open nothing.</summary>
    public const string GithubWorkflowRunYaml = """
        mapping:
          identity_version: 1
          # deployment and deployment_status payloads also carry a workflow_run: they have their own mappings, ordered before this one.
          applies_when: { all: [ { exists: "$.workflow_run" }, { not: { exists: "$.deployment" } } ] }
          fields:
            labels:
              object:
                event:    { header: "X-GitHub-Event" }
                repo:     { path: "$.repository.full_name" }
                pipeline: { path: "$.workflow_run.name" }
                kind:
                  lookup:
                    from: "$.workflow_run.name"
                    # Assumed naming convention until the real names arrive (M16): *-ci / ci-* / build-* → build, deploy-* / *-cd(-env) → deploy, promote-* / *-promotion → promote, else build.
                    regex_map: { "(?i)(ci|build)-.*": build, "(?i).*-ci": build, "(?i)deploy-.*": deploy, "(?i).*-cd(-.*)?": deploy, "(?i)promote-.*": promote, "(?i).*-promotion": promote }
                    default: build
                result:   { path: "$.workflow_run.conclusion" }
                trigger:  { path: "$.workflow_run.event" }
                branch:   { path: "$.workflow_run.head_branch" }
                run:      { path: "$.workflow_run.run_number" }
                attempt:  { path: "$.workflow_run.run_attempt" }
                commit:   { path: "$.workflow_run.head_sha", transform: ["truncate:12"] }
                actor:    { path: "$.workflow_run.actor.login" }
            # GitHub also sends requested / in_progress; without a conclusion they are informational and open nothing.
            event_type:
              lookup: { from: labels.result, map: { success: resolved, failure: firing, timed_out: firing, startup_failure: firing }, default: informational }
            source_alert_id:  { path: "$.workflow_run.id", as: string }
            source_event_id:  { first_nonempty: [ { header: "X-GitHub-Delivery" }, { template: "{$.workflow_run.id}:{$.workflow_run.run_attempt}:{$.workflow_run.updated_at}" } ] }
            occurred_at:      { path: "$.workflow_run.updated_at", as: timestamp }
            environment:
              first_nonempty:
                - { const: ci, when: { eq: [labels.kind, build] } }
                - lookup:
                    from: "$.workflow_run.name"
                    regex_map:
                      "(?i).*(-cd-|deploy-|promote-)(prod|production)(-.*)?": production
                      "(?i).*(-cd-|deploy-|promote-)(stg|stage|staging|uat)(-.*)?": staging
                      "(?i).*(-cd-|deploy-|promote-)(dev|development)(-.*)?": development
                      "(?i).*(-cd-|deploy-|promote-)(pr|eph|preview)-.*": ephemeral
                    default: unknown
            severity:
              first_nonempty:
                - { const: informational, when: { eq: [event_type, informational] } }
                - { lookup: { from: environment, map: { production: high, staging: medium, development: low, ephemeral: low, unknown: medium } }, when: { neq: [labels.kind, build] } }
                - { const: informational, when: { in: [labels.trigger, [pull_request, pull_request_target]] } }
                - { lookup: { from: labels.branch, regex_map: { "main|master": high, "(release|hotfix)/.*": high }, default: low } }
            # A run that neither failed nor succeeded (cancelled, skipped, still running) has no rule: it is recorded, but there is no condition and therefore no episode (04 §2.1).
            rule_id:   { lookup: { from: labels.kind, map: { build: build-failed, deploy: deploy-failed, promote: promotion-failed } }, when: { in: [event_type, [firing, resolved]] } }
            rule_name: { lookup: { from: labels.kind, map: { build: Build failed, deploy: Deployment failed, promote: Promotion failed } } }
            resource_id:   { template: "{$.repository.full_name}:{$.workflow_run.path}" }
            resource_name: { path: "$.workflow_run.name" }
            summary:       { template: "{labels.kind} {labels.repo} / {labels.pipeline} #{labels.run} {labels.result} ({labels.branch})", max_length: 500 }
            source_url:    { path: "$.workflow_run.html_url" }
            dimensions:
              object:
                branch: { template: "{labels.branch}", when: { eq: [labels.kind, build] } }
          required: [event_type, source_alert_id]
          identity:
            - { name: repo,        path: "$.repository.full_name", transform: [lower] }
            - { name: environment, field: environment }
            - { name: pipeline,    path: "$.workflow_run.workflow_id" }
            - { name: rule,        field: rule_id }
            - { name: dims,        field: dimensions, optional: true }
          lifecycle_profile_hint: explicit_recovery
        """;

    /// <summary>GitHub <c>workflow_job</c>: one condition per job, for workflows whose jobs are the environments.</summary>
    public const string GithubWorkflowJobYaml = """
        mapping:
          identity_version: 1
          applies_when: { exists: "$.workflow_job" }
          fields:
            labels:
              object:
                event:    { header: "X-GitHub-Event" }
                repo:     { path: "$.repository.full_name" }
                pipeline: { path: "$.workflow_job.workflow_name" }
                kind:
                  lookup:
                    from: "$.workflow_job.workflow_name"
                    # Assumed naming convention until the real names arrive (M16): *-ci / ci-* / build-* → build, deploy-* / *-cd(-env) → deploy, promote-* / *-promotion → promote, else build.
                    regex_map: { "(?i)(ci|build)-.*": build, "(?i).*-ci": build, "(?i)deploy-.*": deploy, "(?i).*-cd(-.*)?": deploy, "(?i)promote-.*": promote, "(?i).*-promotion": promote }
                    default: build
                job:      { path: "$.workflow_job.name" }
                result:   { path: "$.workflow_job.conclusion" }
                branch:   { path: "$.workflow_job.head_branch" }
                run:      { path: "$.workflow_job.run_id" }
                attempt:  { path: "$.workflow_job.run_attempt" }
                commit:   { path: "$.workflow_job.head_sha", transform: ["truncate:12"] }
            event_type:
              lookup: { from: labels.result, map: { success: resolved, failure: firing, timed_out: firing }, default: informational }
            source_alert_id:  { path: "$.workflow_job.id", as: string }
            source_event_id:  { first_nonempty: [ { header: "X-GitHub-Delivery" }, { template: "{$.workflow_job.id}:{$.workflow_job.run_attempt}:{$.workflow_job.completed_at}" } ] }
            occurred_at:      { first_nonempty: [ { path: "$.workflow_job.completed_at" }, { path: "$.workflow_job.started_at" }, { path: "$.workflow_job.created_at" } ], as: timestamp }
            # A job named after its environment (deploy production, staging, pr-123) wins; otherwise the workflow name decides.
            environment:
              first_nonempty:
                - { const: ci, when: { eq: [labels.kind, build] } }
                - lookup:
                    from: "$.workflow_job.name"
                    regex_map:
                      "(?i)(.*[-_ ])?(prod|production)([-_ ].*)?": production
                      "(?i)(.*[-_ ])?(stg|stage|staging|uat)([-_ ].*)?": staging
                      "(?i)(.*[-_ ])?(dev|development)([-_ ].*)?": development
                      "(?i)(.*[-_ ])?(pr|eph|preview)-.*": ephemeral
                - lookup:
                    from: "$.workflow_job.workflow_name"
                    regex_map:
                      "(?i).*(-cd-|deploy-|promote-)(prod|production)(-.*)?": production
                      "(?i).*(-cd-|deploy-|promote-)(stg|stage|staging|uat)(-.*)?": staging
                      "(?i).*(-cd-|deploy-|promote-)(dev|development)(-.*)?": development
                      "(?i).*(-cd-|deploy-|promote-)(pr|eph|preview)-.*": ephemeral
                    default: unknown
            severity:
              first_nonempty:
                - { const: informational, when: { eq: [event_type, informational] } }
                - { lookup: { from: environment, map: { production: high, staging: medium, development: low, ephemeral: low, unknown: medium } }, when: { neq: [labels.kind, build] } }
                - { lookup: { from: labels.branch, regex_map: { "main|master": high, "(release|hotfix)/.*": high }, default: low } }
            # A run that neither failed nor succeeded (cancelled, skipped, still running) has no rule: it is recorded, but there is no condition and therefore no episode (04 §2.1).
            rule_id:   { lookup: { from: labels.kind, map: { build: build-failed, deploy: deploy-failed, promote: promotion-failed } }, when: { in: [event_type, [firing, resolved]] } }
            rule_name: { lookup: { from: labels.kind, map: { build: Build failed, deploy: Deployment failed, promote: Promotion failed } } }
            resource_id:   { template: "{$.repository.full_name}:{$.workflow_job.workflow_name}" }
            resource_name: { path: "$.workflow_job.workflow_name" }
            summary:       { template: "{labels.kind} {labels.repo} / {labels.pipeline} job {labels.job} {labels.result} ({labels.branch})", max_length: 500 }
            source_url:    { path: "$.workflow_job.html_url" }
            dimensions:
              object:
                branch: { template: "{labels.branch}", when: { eq: [labels.kind, build] } }
                job:    { path: "$.workflow_job.name" }
          required: [event_type, source_alert_id]
          identity:
            - { name: repo,        path: "$.repository.full_name", transform: [lower] }
            - { name: environment, field: environment }
            - { name: pipeline,    path: "$.workflow_job.workflow_name", transform: [lower] }
            - { name: rule,        field: rule_id }
            - { name: dims,        field: dimensions }
          lifecycle_profile_hint: explicit_recovery
        """;

    /// <summary>GitHub <c>deployment_status</c>: the deployment condition of one repository in one environment.</summary>
    public const string GithubDeploymentStatusYaml = """
        mapping:
          identity_version: 1
          applies_when: { exists: "$.deployment_status" }
          fields:
            labels:
              object:
                event:    { header: "X-GitHub-Event" }
                repo:     { path: "$.repository.full_name" }
                pipeline: { first_nonempty: [ { path: "$.workflow.name" }, { path: "$.deployment.task" } ] }
                kind:     { const: deploy }
                target:   { path: "$.deployment.environment" }
                result:   { path: "$.deployment_status.state" }
                ref:      { path: "$.deployment.ref" }
                commit:   { path: "$.deployment.sha", transform: ["truncate:12"] }
                actor:    { path: "$.deployment_status.creator.login" }
                detail:   { path: "$.deployment_status.description" }
            # pending / queued / in_progress / waiting / inactive are facts about a rollout, not a condition: informational, no episode.
            event_type:
              lookup: { from: labels.result, map: { success: resolved, failure: firing, error: firing }, default: informational }
            source_alert_id:  { path: "$.deployment.id", as: string }
            source_event_id:  { first_nonempty: [ { header: "X-GitHub-Delivery" }, { template: "{$.deployment.id}:{$.deployment_status.id}" } ] }
            occurred_at:      { first_nonempty: [ { path: "$.deployment_status.updated_at" }, { path: "$.deployment_status.created_at" } ], as: timestamp }
            environment:
              first_nonempty:
                - lookup:
                    from: "$.deployment.environment"
                    regex_map:
                      "(?i)(.*[-_ ])?(prod|production)([-_ ].*)?": production
                      "(?i)(.*[-_ ])?(stg|stage|staging|uat)([-_ ].*)?": staging
                      "(?i)(.*[-_ ])?(dev|development)([-_ ].*)?": development
                      "(?i)(.*[-_ ])?(pr|eph|preview)-.*": ephemeral
                - { const: unknown }
            severity:
              first_nonempty:
                - { const: informational, when: { eq: [event_type, informational] } }
                - { lookup: { from: environment, map: { production: high, staging: medium, development: low, ephemeral: low }, default: medium } }
            rule_id:   { const: deploy-failed, when: { in: [event_type, [firing, resolved]] } }
            rule_name: { const: Deployment failed }
            resource_id:   { template: "{$.repository.full_name}:{$.deployment.environment}" }
            resource_name: { path: "$.deployment.environment" }
            summary:       { template: "deploy {labels.repo} to {labels.target}: {labels.result} ({labels.detail})", max_length: 500 }
            source_url:    { first_nonempty: [ { path: "$.deployment_status.log_url" }, { path: "$.deployment_status.target_url" }, { path: "$.workflow_run.html_url" }, { path: "$.repository.html_url" } ] }
          required: [event_type, source_alert_id]
          identity:
            - { name: repo,        path: "$.repository.full_name", transform: [lower] }
            - { name: environment, field: environment }
            - { name: target,      path: "$.deployment.environment", transform: [lower] }
            - { name: rule,        field: rule_id }
          lifecycle_profile_hint: explicit_recovery
        """;

    /// <summary>GitHub <c>deployment</c> (created): the start of a rollout, recorded without an episode.</summary>
    public const string GithubDeploymentYaml = """
        mapping:
          identity_version: 1
          applies_when: { all: [ { exists: "$.deployment" }, { not: { exists: "$.deployment_status" } } ] }
          fields:
            labels:
              object:
                event:    { header: "X-GitHub-Event" }
                repo:     { path: "$.repository.full_name" }
                kind:     { const: deploy }
                target:   { path: "$.deployment.environment" }
                ref:      { path: "$.deployment.ref" }
                actor:    { path: "$.deployment.creator.login" }
            # A requested deployment is a fact, not a condition: recorded for the audit trail, never an episode — the rule is deliberately empty, so the identity cannot resolve (04 §2.1).
            rule_id:          { const: null }
            event_type:       { const: informational }
            severity:         { const: informational }
            source_alert_id:  { path: "$.deployment.id", as: string }
            source_event_id:  { first_nonempty: [ { header: "X-GitHub-Delivery" }, { template: "{$.deployment.id}:created" } ] }
            occurred_at:      { path: "$.deployment.created_at", as: timestamp }
            environment:
              first_nonempty:
                - lookup:
                    from: "$.deployment.environment"
                    regex_map:
                      "(?i)(.*[-_ ])?(prod|production)([-_ ].*)?": production
                      "(?i)(.*[-_ ])?(stg|stage|staging|uat)([-_ ].*)?": staging
                      "(?i)(.*[-_ ])?(dev|development)([-_ ].*)?": development
                      "(?i)(.*[-_ ])?(pr|eph|preview)-.*": ephemeral
                - { const: unknown }
            resource_name:    { path: "$.deployment.environment" }
            summary:          { template: "deployment of {labels.repo} to {labels.target} requested ({labels.ref})", max_length: 500 }
            source_url:       { path: "$.repository.html_url" }
          required: [event_type, source_alert_id]
          identity:
            - { name: rule, field: rule_id }
          lifecycle_profile_hint: one_shot
        """;

    /// <summary>GitHub <c>ping</c> on webhook creation: recorded without an episode instead of a quarantine entry.</summary>
    public const string GithubPingYaml = """
        mapping:
          identity_version: 1
          applies_when: { exists: "$.zen" }
          fields:
            labels:
              object:
                event:    { header: "X-GitHub-Event" }
                repo:     { path: "$.repository.full_name" }
                events:   { path: "$.hook.events", as: string }
            # The ping GitHub sends when the webhook is created: recorded so the Failures tab stays empty, never an episode — the rule is deliberately empty, so the identity cannot resolve.
            rule_id:          { const: null }
            event_type:       { const: informational }
            severity:         { const: informational }
            source_alert_id:  { path: "$.hook_id", as: string }
            source_event_id:  { first_nonempty: [ { header: "X-GitHub-Delivery" }, { template: "{$.hook_id}:ping" } ] }
            summary:          { template: "GitHub webhook ping for {labels.repo}", max_length: 500 }
            source_url:       { path: "$.repository.html_url" }
          required: [event_type, source_alert_id]
          identity:
            - { name: rule, field: rule_id }
          lifecycle_profile_hint: one_shot
        """;

    /// <summary>Same bytes as <c>tests/fixtures/azure-devops/PROVISIONAL-build-complete-failed.json</c> (vendor-documented shape, PROVISIONAL until a real capture replaces it).</summary>
    public const string AzureDevopsBuildFailedSample = """
        { "subscriptionId": "00000000-0000-0000-0000-0000000000a1", "notificationId": 7, "id": "PROVISIONAL-ado-build-1",
          "eventType": "build.complete", "publisherId": "tfs",
          "message": { "text": "Build orders-ci 20261021.3 failed" },
          "detailedMessage": { "text": "Build orders-ci 20261021.3 failed" },
          "resource": {
            "_links": { "web": { "href": "https://dev.azure.com/fabrikam/Orders/_build/results?buildId=4123" } },
            "id": 4123, "buildNumber": "20261021.3", "status": "completed", "result": "failed", "reason": "individualCI",
            "queueTime": "2026-10-21T09:58:00Z", "startTime": "2026-10-21T09:58:30Z", "finishTime": "2026-10-21T10:04:12Z",
            "url": "https://dev.azure.com/fabrikam/11111111-2222-3333-4444-555555555555/_apis/build/Builds/4123",
            "definition": { "id": 42, "name": "orders-ci", "path": "\\", "type": "build" },
            "project": { "id": "11111111-2222-3333-4444-555555555555", "name": "Orders" },
            "sourceBranch": "refs/heads/main", "sourceVersion": "9fceb02d0ae598e95dc970b74767f19372d61af8",
            "requestedFor": { "displayName": "Dev One", "uniqueName": "dev.one@example.com" },
            "lastChangedDate": "2026-10-21T10:04:12Z",
            "repository": { "id": "66666666-7777-8888-9999-000000000000", "name": "orders", "type": "TfsGit" } },
          "resourceVersion": "2.0",
          "resourceContainers": { "collection": { "id": "c0000000-0000-0000-0000-000000000001" }, "account": { "id": "a0000000-0000-0000-0000-000000000001" }, "project": { "id": "11111111-2222-3333-4444-555555555555" } },
          "createdDate": "2026-10-21T10:04:13Z" }
        """;

    /// <summary>Same bytes as <c>tests/fixtures/azure-devops/PROVISIONAL-build-complete-succeeded.json</c> (vendor-documented shape, PROVISIONAL until a real capture replaces it).</summary>
    public const string AzureDevopsBuildSucceededSample = """
        { "subscriptionId": "00000000-0000-0000-0000-0000000000a1", "notificationId": 7, "id": "PROVISIONAL-ado-build-2",
          "eventType": "build.complete", "publisherId": "tfs",
          "message": { "text": "Build orders-ci 20261021.4 succeeded" },
          "detailedMessage": { "text": "Build orders-ci 20261021.4 succeeded" },
          "resource": {
            "_links": { "web": { "href": "https://dev.azure.com/fabrikam/Orders/_build/results?buildId=4124" } },
            "id": 4124, "buildNumber": "20261021.4", "status": "completed", "result": "succeeded", "reason": "individualCI",
            "queueTime": "2026-10-21T10:08:00Z", "startTime": "2026-10-21T10:08:30Z", "finishTime": "2026-10-21T10:14:12Z",
            "url": "https://dev.azure.com/fabrikam/11111111-2222-3333-4444-555555555555/_apis/build/Builds/4124",
            "definition": { "id": 42, "name": "orders-ci", "path": "\\", "type": "build" },
            "project": { "id": "11111111-2222-3333-4444-555555555555", "name": "Orders" },
            "sourceBranch": "refs/heads/main", "sourceVersion": "9fceb02d0ae598e95dc970b74767f19372d61af8",
            "requestedFor": { "displayName": "Dev One", "uniqueName": "dev.one@example.com" },
            "lastChangedDate": "2026-10-21T10:14:12Z",
            "repository": { "id": "66666666-7777-8888-9999-000000000000", "name": "orders", "type": "TfsGit" } },
          "resourceVersion": "2.0",
          "resourceContainers": { "collection": { "id": "c0000000-0000-0000-0000-000000000001" }, "account": { "id": "a0000000-0000-0000-0000-000000000001" }, "project": { "id": "11111111-2222-3333-4444-555555555555" } },
          "createdDate": "2026-10-21T10:14:13Z" }
        """;

    /// <summary>Same bytes as <c>tests/fixtures/azure-devops/PROVISIONAL-run-state-changed-failed.json</c> (vendor-documented shape, PROVISIONAL until a real capture replaces it).</summary>
    public const string AzureDevopsRunFailedSample = """
        { "id": "PROVISIONAL-ado-run-1", "eventType": "ms.vss-pipelines.run-state-changed-event", "publisherId": "pipelines",
          "message": { "text": "Run 20261021.2 failed" }, "detailedMessage": { "text": "Run 20261021.2 failed" },
          "resource": {
            "run": {
              "_links": { "self": { "href": "https://dev.azure.com/fabrikam/Orders/_apis/pipelines/7/runs/9001" }, "web": { "href": "https://dev.azure.com/fabrikam/Orders/_build/results?buildId=9001" } },
              "pipeline": { "url": "https://dev.azure.com/fabrikam/Orders/_apis/pipelines/7?revision=3", "id": 7, "revision": 3, "name": "orders-cd-prod", "folder": "\\" },
              "state": "completed", "result": "failed",
              "createdDate": "2026-10-21T11:00:00Z", "finishedDate": "2026-10-21T11:07:40Z",
              "url": "https://dev.azure.com/fabrikam/Orders/_apis/pipelines/7/runs/9001", "id": 9001, "name": "20261021.2",
              "resources": { "repositories": { "self": { "repository": { "id": "66666666-7777-8888-9999-000000000000", "type": "azureReposGit" }, "refName": "refs/heads/main", "version": "9fceb02d0ae598e95dc970b74767f19372d61af8" } } } },
            "pipeline": { "url": "https://dev.azure.com/fabrikam/Orders/_apis/pipelines/7?revision=3", "id": 7, "revision": 3, "name": "orders-cd-prod", "folder": "\\" },
            "runUrl": "https://dev.azure.com/fabrikam/Orders/_build/results?buildId=9001" },
          "resourceVersion": "5.1-preview.1",
          "resourceContainers": { "collection": { "id": "c0000000-0000-0000-0000-000000000001" }, "account": { "id": "a0000000-0000-0000-0000-000000000001" }, "project": { "id": "11111111-2222-3333-4444-555555555555" } },
          "createdDate": "2026-10-21T11:07:41Z" }
        """;

    /// <summary>Same bytes as <c>tests/fixtures/azure-devops/PROVISIONAL-stage-state-changed-failed.json</c> (vendor-documented shape, PROVISIONAL until a real capture replaces it).</summary>
    public const string AzureDevopsStageFailedSample = """
        { "id": "PROVISIONAL-ado-stage-1", "eventType": "ms.vss-pipelines.stage-state-changed-event", "publisherId": "pipelines",
          "message": { "text": "Run 20261021.5 stage production failed" }, "detailedMessage": { "text": "Run 20261021.5 stage production failed" },
          "resource": {
            "stage": {
              "_links": { "web": { "href": "https://dev.azure.com/fabrikam/Orders/_build/results?buildId=9105&view=logs&s=stage-production" }, "pipeline.web": { "href": "https://dev.azure.com/fabrikam/Orders/_build/definition?definitionId=8" } },
              "id": "4e1a3b2c-0000-0000-0000-000000000001", "name": "production", "displayName": "production", "state": "completed", "result": "failed" },
            "run": {
              "_links": { "web": { "href": "https://dev.azure.com/fabrikam/Orders/_build/results?buildId=9105" } },
              "pipeline": { "url": "https://dev.azure.com/fabrikam/Orders/_apis/pipelines/8?revision=12", "id": 8, "revision": 12, "name": "orders-cd", "folder": "\\" },
              "state": "inProgress", "createdDate": "2026-10-21T12:00:00Z",
              "url": "https://dev.azure.com/fabrikam/Orders/_apis/pipelines/8/runs/9105", "id": 9105, "name": "20261021.5",
              "resources": { "repositories": { "self": { "refName": "refs/heads/main", "version": "9fceb02d0ae598e95dc970b74767f19372d61af8" } } } },
            "pipeline": { "url": "https://dev.azure.com/fabrikam/Orders/_apis/pipelines/8?revision=12", "id": 8, "revision": 12, "name": "orders-cd", "folder": "\\" },
            "runUrl": "https://dev.azure.com/fabrikam/Orders/_build/results?buildId=9105" },
          "resourceVersion": "5.1-preview.1",
          "resourceContainers": { "collection": { "id": "c0000000-0000-0000-0000-000000000001" }, "account": { "id": "a0000000-0000-0000-0000-000000000001" }, "project": { "id": "11111111-2222-3333-4444-555555555555" } },
          "createdDate": "2026-10-21T12:09:30Z" }
        """;

    /// <summary>Same bytes as <c>tests/fixtures/azure-devops/PROVISIONAL-deployment-completed-failed.json</c> (vendor-documented shape, PROVISIONAL until a real capture replaces it).</summary>
    public const string AzureDevopsReleaseFailedSample = """
        { "id": "PROVISIONAL-ado-release-1", "eventType": "ms.vss-release.deployment-completed-event", "publisherId": "rm",
          "message": { "text": "Deployment of release Release-57 on stage Production failed." },
          "detailedMessage": { "text": "Deployment of release Release-57 on stage Production failed." },
          "resource": {
            "environment": {
              "id": 1557, "releaseId": 57, "name": "Production", "status": "rejected", "rank": 3, "definitionEnvironmentId": 3,
              "createdOn": "2026-10-21T13:00:00Z", "modifiedOn": "2026-10-21T13:12:05Z", "triggerReason": "Manual",
              "release": { "id": 57, "name": "Release-57", "url": "https://vsrm.dev.azure.com/fabrikam/Orders/_apis/Release/releases/57",
                "_links": { "web": { "href": "https://dev.azure.com/fabrikam/Orders/_release?releaseId=57&_a=release-summary" } } },
              "releaseDefinition": { "id": 5, "name": "orders-release", "url": "https://vsrm.dev.azure.com/fabrikam/Orders/_apis/Release/definitions/5",
                "_links": { "web": { "href": "https://dev.azure.com/fabrikam/Orders/_release?definitionId=5" } } },
              "releaseCreatedBy": { "displayName": "Release Bot", "uniqueName": "release.bot@example.com" } },
            "project": { "id": "11111111-2222-3333-4444-555555555555", "name": "Orders" },
            "deployment": { "id": 3012, "definitionEnvironmentId": 3, "attempt": 1, "deploymentStatus": "failed", "operationStatus": "Rejected",
              "release": { "id": 57, "name": "Release-57" }, "releaseEnvironment": { "id": 1557, "name": "Production" },
              "queuedOn": "2026-10-21T13:00:10Z", "startedOn": "2026-10-21T13:00:30Z", "completedOn": "2026-10-21T13:12:05Z",
              "requestedBy": { "displayName": "Release Bot" } },
            "stageName": "Production",
            "url": "https://vsrm.dev.azure.com/fabrikam/Orders/_apis/Release/releases/57" },
          "resourceVersion": "3.0-preview.1",
          "resourceContainers": { "collection": { "id": "c0000000-0000-0000-0000-000000000001" }, "account": { "id": "a0000000-0000-0000-0000-000000000001" }, "project": { "id": "11111111-2222-3333-4444-555555555555" } },
          "createdDate": "2026-10-21T13:12:06Z" }
        """;

    /// <summary>Same bytes as <c>tests/fixtures/github/PROVISIONAL-workflow-run-failure.json</c> (vendor-documented shape, PROVISIONAL until a real capture replaces it).</summary>
    public const string GithubWorkflowRunFailureSample = """
        { "action": "completed",
          "workflow_run": {
            "id": 30433642, "name": "orders-ci", "node_id": "MDExOldvcmtmbG93IFJ1bjI2OTI4OQ==",
            "head_branch": "main", "head_sha": "acb5820ced9479c074f688cc328bf03f341a511d", "path": ".github/workflows/ci.yml",
            "display_title": "Fix order totals", "run_number": 562, "event": "push", "status": "completed", "conclusion": "failure",
            "workflow_id": 159038, "check_suite_id": 414944374, "run_attempt": 1,
            "url": "https://api.github.com/repos/octo-org/orders/actions/runs/30433642",
            "html_url": "https://github.com/octo-org/orders/actions/runs/30433642",
            "created_at": "2026-10-21T10:00:00Z", "updated_at": "2026-10-21T10:05:00Z", "run_started_at": "2026-10-21T10:00:05Z",
            "actor": { "login": "octocat", "id": 1 }, "triggering_actor": { "login": "octocat", "id": 1 },
            "head_commit": { "id": "acb5820ced9479c074f688cc328bf03f341a511d", "message": "Fix order totals", "author": { "name": "Octo Cat", "email": "octocat@example.com" } },
            "repository": { "id": 1296269, "name": "orders", "full_name": "octo-org/orders" },
            "head_repository": { "id": 1296269, "name": "orders", "full_name": "octo-org/orders" } },
          "workflow": { "id": 159038, "node_id": "MDg6V29ya2Zsb3cxNTkwMzg=", "name": "orders-ci", "path": ".github/workflows/ci.yml", "state": "active",
            "html_url": "https://github.com/octo-org/orders/blob/main/.github/workflows/ci.yml" },
          "repository": { "id": 1296269, "name": "orders", "full_name": "octo-org/orders", "private": true, "html_url": "https://github.com/octo-org/orders", "default_branch": "main" },
          "organization": { "login": "octo-org", "id": 6811672 },
          "sender": { "login": "octocat", "id": 1 } }
        """;

    /// <summary>Same bytes as <c>tests/fixtures/github/PROVISIONAL-workflow-run-success.json</c> (vendor-documented shape, PROVISIONAL until a real capture replaces it).</summary>
    public const string GithubWorkflowRunSuccessSample = """
        { "action": "completed",
          "workflow_run": {
            "id": 30433642, "name": "orders-ci", "node_id": "MDExOldvcmtmbG93IFJ1bjI2OTI4OQ==",
            "head_branch": "main", "head_sha": "acb5820ced9479c074f688cc328bf03f341a511d", "path": ".github/workflows/ci.yml",
            "display_title": "Fix order totals", "run_number": 562, "event": "push", "status": "completed", "conclusion": "success",
            "workflow_id": 159038, "check_suite_id": 414944374, "run_attempt": 2,
            "url": "https://api.github.com/repos/octo-org/orders/actions/runs/30433642",
            "html_url": "https://github.com/octo-org/orders/actions/runs/30433642",
            "created_at": "2026-10-21T10:00:00Z", "updated_at": "2026-10-21T10:10:00Z", "run_started_at": "2026-10-21T10:00:05Z",
            "actor": { "login": "octocat", "id": 1 }, "triggering_actor": { "login": "octocat", "id": 1 },
            "head_commit": { "id": "acb5820ced9479c074f688cc328bf03f341a511d", "message": "Fix order totals", "author": { "name": "Octo Cat", "email": "octocat@example.com" } },
            "repository": { "id": 1296269, "name": "orders", "full_name": "octo-org/orders" },
            "head_repository": { "id": 1296269, "name": "orders", "full_name": "octo-org/orders" } },
          "workflow": { "id": 159038, "node_id": "MDg6V29ya2Zsb3cxNTkwMzg=", "name": "orders-ci", "path": ".github/workflows/ci.yml", "state": "active",
            "html_url": "https://github.com/octo-org/orders/blob/main/.github/workflows/ci.yml" },
          "repository": { "id": 1296269, "name": "orders", "full_name": "octo-org/orders", "private": true, "html_url": "https://github.com/octo-org/orders", "default_branch": "main" },
          "organization": { "login": "octo-org", "id": 6811672 },
          "sender": { "login": "octocat", "id": 1 } }
        """;

    /// <summary>Same bytes as <c>tests/fixtures/github/PROVISIONAL-workflow-job-failure.json</c> (vendor-documented shape, PROVISIONAL until a real capture replaces it).</summary>
    public const string GithubWorkflowJobFailureSample = """
        { "action": "completed",
          "workflow_job": {
            "id": 2832938, "run_id": 30433642, "workflow_name": "orders-ci", "head_branch": "main",
            "run_url": "https://api.github.com/repos/octo-org/orders/actions/runs/30433642", "run_attempt": 1,
            "node_id": "MDg6Q2hlY2tSdW4yODMyOTM4", "head_sha": "acb5820ced9479c074f688cc328bf03f341a511d",
            "url": "https://api.github.com/repos/octo-org/orders/actions/jobs/2832938",
            "html_url": "https://github.com/octo-org/orders/actions/runs/30433642/job/2832938",
            "status": "completed", "conclusion": "failure", "created_at": "2026-10-21T10:00:10Z", "started_at": "2026-10-21T10:00:30Z", "completed_at": "2026-10-21T10:04:00Z",
            "name": "test (unit)",
            "steps": [
              { "name": "Set up job", "status": "completed", "conclusion": "success", "number": 1, "started_at": "2026-10-21T10:00:30Z", "completed_at": "2026-10-21T10:00:35Z" },
              { "name": "Run tests", "status": "completed", "conclusion": "failure", "number": 2, "started_at": "2026-10-21T10:00:35Z", "completed_at": "2026-10-21T10:03:58Z" } ],
            "check_run_url": "https://api.github.com/repos/octo-org/orders/check-runs/2832938",
            "labels": [ "ubuntu-latest" ], "runner_id": 1, "runner_name": "GitHub Actions 1", "runner_group_id": 2, "runner_group_name": "GitHub Actions" },
          "repository": { "id": 1296269, "name": "orders", "full_name": "octo-org/orders", "private": true, "html_url": "https://github.com/octo-org/orders", "default_branch": "main" },
          "organization": { "login": "octo-org", "id": 6811672 },
          "sender": { "login": "octocat", "id": 1 } }
        """;

    /// <summary>Same bytes as <c>tests/fixtures/github/PROVISIONAL-deployment-status-failure.json</c> (vendor-documented shape, PROVISIONAL until a real capture replaces it).</summary>
    public const string GithubDeploymentStatusFailureSample = """
        { "action": "created",
          "deployment_status": {
            "url": "https://api.github.com/repos/octo-org/orders/deployments/145988746/statuses/209916", "id": 209916, "node_id": "MDE2OkRlcGxveW1lbnRTdGF0dXMyMDk5MTY=",
            "state": "failure", "creator": { "login": "github-actions[bot]", "id": 41898282, "type": "Bot" },
            "description": "helm upgrade --atomic rolled back", "environment": "production",
            "target_url": "https://github.com/octo-org/orders/actions/runs/30433700", "log_url": "https://github.com/octo-org/orders/actions/runs/30433700",
            "created_at": "2026-10-21T14:08:00Z", "updated_at": "2026-10-21T14:08:00Z",
            "deployment_url": "https://api.github.com/repos/octo-org/orders/deployments/145988746",
            "repository_url": "https://api.github.com/repos/octo-org/orders" },
          "deployment": {
            "url": "https://api.github.com/repos/octo-org/orders/deployments/145988746", "id": 145988746, "node_id": "MDEwOkRlcGxveW1lbnQxNDU5ODg3NDY=",
            "sha": "acb5820ced9479c074f688cc328bf03f341a511d", "ref": "main", "task": "deploy", "payload": {},
            "original_environment": "production", "environment": "production", "description": null,
            "creator": { "login": "github-actions[bot]", "id": 41898282, "type": "Bot" },
            "created_at": "2026-10-21T14:00:00Z", "updated_at": "2026-10-21T14:00:00Z",
            "statuses_url": "https://api.github.com/repos/octo-org/orders/deployments/145988746/statuses",
            "repository_url": "https://api.github.com/repos/octo-org/orders" },
          "workflow": { "id": 159040, "name": "orders-cd", "path": ".github/workflows/cd.yml", "state": "active" },
          "workflow_run": { "id": 30433700, "name": "orders-cd", "head_branch": "main", "run_number": 88, "event": "push", "status": "completed", "conclusion": "failure",
            "html_url": "https://github.com/octo-org/orders/actions/runs/30433700", "updated_at": "2026-10-21T14:08:00Z" },
          "repository": { "id": 1296269, "name": "orders", "full_name": "octo-org/orders", "private": true, "html_url": "https://github.com/octo-org/orders", "default_branch": "main" },
          "organization": { "login": "octo-org", "id": 6811672 },
          "sender": { "login": "github-actions[bot]", "id": 41898282, "type": "Bot" } }
        """;

    /// <summary>Same bytes as <c>tests/fixtures/github/PROVISIONAL-deployment-created.json</c> (vendor-documented shape, PROVISIONAL until a real capture replaces it).</summary>
    public const string GithubDeploymentCreatedSample = """
        { "action": "created",
          "deployment": {
            "url": "https://api.github.com/repos/octo-org/orders/deployments/145988746", "id": 145988746, "node_id": "MDEwOkRlcGxveW1lbnQxNDU5ODg3NDY=",
            "sha": "acb5820ced9479c074f688cc328bf03f341a511d", "ref": "main", "task": "deploy", "payload": {},
            "original_environment": "production", "environment": "production", "description": null,
            "creator": { "login": "github-actions[bot]", "id": 41898282, "type": "Bot" },
            "created_at": "2026-10-21T14:00:00Z", "updated_at": "2026-10-21T14:00:00Z",
            "statuses_url": "https://api.github.com/repos/octo-org/orders/deployments/145988746/statuses",
            "repository_url": "https://api.github.com/repos/octo-org/orders" },
          "workflow": { "id": 159040, "name": "orders-cd", "path": ".github/workflows/cd.yml", "state": "active" },
          "workflow_run": { "id": 30433700, "name": "orders-cd", "head_branch": "main", "run_number": 88, "event": "push", "status": "in_progress", "conclusion": null,
            "html_url": "https://github.com/octo-org/orders/actions/runs/30433700", "updated_at": "2026-10-21T14:00:00Z" },
          "repository": { "id": 1296269, "name": "orders", "full_name": "octo-org/orders", "private": true, "html_url": "https://github.com/octo-org/orders", "default_branch": "main" },
          "organization": { "login": "octo-org", "id": 6811672 },
          "sender": { "login": "github-actions[bot]", "id": 41898282, "type": "Bot" } }
        """;

    /// <summary>Same bytes as <c>tests/fixtures/github/PROVISIONAL-ping.json</c> (vendor-documented shape, PROVISIONAL until a real capture replaces it).</summary>
    public const string GithubPingSample = """
        { "zen": "Keep it logically awesome.", "hook_id": 490000001,
          "hook": { "type": "Repository", "id": 490000001, "name": "web", "active": true, "events": [ "workflow_run" ],
            "config": { "content_type": "json", "insecure_ssl": "0", "url": "https://denoise.example.com/ingest/k0000000000000000" },
            "created_at": "2026-10-21T09:00:00Z", "updated_at": "2026-10-21T09:00:00Z" },
          "repository": { "id": 1296269, "name": "orders", "full_name": "octo-org/orders", "private": true, "html_url": "https://github.com/octo-org/orders", "default_branch": "main" },
          "sender": { "login": "octocat", "id": 1 } }
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
        IntegrationTypes.AzureDevops =>
        [
            new ReferenceMapping("azure-devops-build", 10, AzureDevopsBuildYaml,
            [
                Sample("PROVISIONAL-build-complete-failed", AzureDevopsBuildFailedSample, null, ("event_type", "firing"), ("severity", "high"), ("environment", "ci"), ("rule_id", "build-failed"), ("resource_name", "orders-ci")),
                Sample("PROVISIONAL-build-complete-succeeded", AzureDevopsBuildSucceededSample, null, ("event_type", "resolved"), ("rule_id", "build-failed")),
            ]),
            new ReferenceMapping("azure-devops-yaml-run", 20, AzureDevopsRunYaml,
            [
                Sample("PROVISIONAL-run-state-changed-failed", AzureDevopsRunFailedSample, null, ("event_type", "firing"), ("severity", "high"), ("environment", "production"), ("rule_id", "deploy-failed")),
            ]),
            new ReferenceMapping("azure-devops-yaml-stage", 30, AzureDevopsStageYaml,
            [
                Sample("PROVISIONAL-stage-state-changed-failed", AzureDevopsStageFailedSample, null, ("event_type", "firing"), ("severity", "high"), ("environment", "production"), ("rule_id", "deploy-failed")),
            ]),
            new ReferenceMapping("azure-devops-release-deployment", 40, AzureDevopsReleaseYaml,
            [
                Sample("PROVISIONAL-deployment-completed-failed", AzureDevopsReleaseFailedSample, null, ("event_type", "firing"), ("severity", "high"), ("environment", "production"), ("rule_id", "deploy-failed"), ("resource_name", "orders-release")),
            ]),
        ],
        IntegrationTypes.Github =>
        [
            new ReferenceMapping("github-ping", 5, GithubPingYaml, [Sample("PROVISIONAL-ping", GithubPingSample, GithubHeaders("ping"), ("event_type", "informational"))]),
            new ReferenceMapping("github-deployment-status", 10, GithubDeploymentStatusYaml,
            [
                Sample("PROVISIONAL-deployment-status-failure", GithubDeploymentStatusFailureSample, GithubHeaders("deployment_status"), ("event_type", "firing"), ("severity", "high"), ("environment", "production"), ("rule_id", "deploy-failed")),
            ]),
            new ReferenceMapping("github-deployment", 20, GithubDeploymentYaml, [Sample("PROVISIONAL-deployment-created", GithubDeploymentCreatedSample, GithubHeaders("deployment"), ("event_type", "informational"))]),
            new ReferenceMapping("github-workflow-job", 30, GithubWorkflowJobYaml,
            [
                Sample("PROVISIONAL-workflow-job-failure", GithubWorkflowJobFailureSample, GithubHeaders("workflow_job"), ("event_type", "firing"), ("severity", "high"), ("environment", "ci"), ("rule_id", "build-failed")),
            ]),
            new ReferenceMapping("github-workflow-run", 40, GithubWorkflowRunYaml,
            [
                Sample("PROVISIONAL-workflow-run-failure", GithubWorkflowRunFailureSample, GithubHeaders("workflow_run"), ("event_type", "firing"), ("severity", "high"), ("environment", "ci"), ("rule_id", "build-failed"), ("resource_name", "orders-ci")),
                Sample("PROVISIONAL-workflow-run-success", GithubWorkflowRunSuccessSample, GithubHeaders("workflow_run", "9c0b8e44-0000-4000-8000-000000000002"), ("event_type", "resolved"), ("rule_id", "build-failed")),
            ]),
        ],
        _ => [],
    };

    private static Dictionary<string, string> GithubHeaders(string eventName, string delivery = "9c0b8e44-0000-4000-8000-000000000001")
        => new(StringComparer.OrdinalIgnoreCase) { ["X-GitHub-Event"] = eventName, ["X-GitHub-Delivery"] = delivery };

    private static MappingSample Sample(string name, string json, IReadOnlyDictionary<string, string>? headers, params (string Field, string Expected)[] expected)
        => new(name, JsonNode.Parse(json)!, headers, expected.ToDictionary(e => e.Field, e => e.Expected, StringComparer.Ordinal));
}
