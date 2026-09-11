namespace AlertHub.Application.Mapping;

/// <summary>Mappings shipped with the Hub. The generic webhook contract (07 §4) is the default for integrations of type <c>generic_webhook</c> that have no mapping of their own.</summary>
public static class BuiltInMappings
{
    public const string GenericWebhookYaml = """
        # Built-in mapping for the recommended generic webhook contract (docs/07-mapping-dsl.md §4).
        mapping:
          identity_version: 1
          fields:
            event_type:       { path: "$.eventType", transform: [lower, trim] }
            source_alert_id:  { path: "$.alertId" }
            source_event_id:  { path: "$.eventId" }
            source_version:   { path: "$.version", as: string }
            occurred_at:      { first_nonempty: [ { path: "$.occurredAt" }, { header: "X-AlertHub-Source-Time" } ], as: timestamp }
            severity:         { path: "$.severity", transform: [lower, trim], default: unknown }
            source_severity:  { path: "$.severity", as: string }
            summary:          { first_nonempty: [ { path: "$.summary" }, { path: "$.rule.name" }, { path: "$.alertId" } ], max_length: 500 }
            resource_id:      { first_nonempty: [ { path: "$.resource.id" }, { path: "$.resource.name" } ], transform: [trim] }
            resource_name:    { first_nonempty: [ { path: "$.resource.name" }, { path: "$.resource.id" } ] }
            rule_id:          { first_nonempty: [ { path: "$.rule.id" }, { path: "$.rule.name" }, { path: "$.alertId" } ], transform: [trim] }
            rule_name:        { first_nonempty: [ { path: "$.rule.name" }, { path: "$.rule.id" } ] }
            environment:      { path: "$.environment", transform: [lower, trim], default: unknown }
            service:          { path: "$.service" }
            source_url:       { path: "$.url" }
            dimensions:       { path: "$.dimensions", as: object }
            labels:           { path: "$.labels", as: object }
          required: [event_type, source_alert_id]
          identity:
            - { name: environment, field: environment }
            - { name: resource,    field: resource_id, optional: true }
            - { name: rule,        field: rule_id }
            - { name: dims,        field: dimensions, optional: true }
          lifecycle_profile_hint: unknown
        """;

    private static readonly Lazy<MappingDocument> GenericWebhookDocument = new(() => MappingParser.ParseYaml(GenericWebhookYaml, versionOverride: 0));

    /// <summary>Version 0 marks a built-in (unstored) mapping in <c>normalised_event.mapping_version</c>.</summary>
    public static MappingDocument GenericWebhook => GenericWebhookDocument.Value;
}
