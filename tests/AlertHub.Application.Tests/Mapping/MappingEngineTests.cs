using System.Text;
using System.Text.Json.Nodes;
using AlertHub.Application.Mapping;
using AlertHub.Domain.Alerts;
using AlertHub.Domain.Common;

namespace AlertHub.Application.Tests.Mapping;

[Trait("Category", "Unit")]
public sealed class MappingEngineTests
{
    private static readonly Guid IntegrationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Received = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    private static MappingResult Run(MappingDocument doc, string json, IReadOnlyDictionary<string, string>? headers = null)
        => MappingEngine.Normalise(doc, new RawInput(Encoding.UTF8.GetBytes(json), headers ?? new Dictionary<string, string>()),
            new MappingContext(IntegrationId, Guid.NewGuid(), Received, doc.Version));

    [Fact]
    public void Generic_webhook_builtin_maps_the_recommended_contract()
    {
        var json = """
            { "eventType":"firing", "alertId":"orders-api-5xx", "eventId":"e-1", "occurredAt":"2026-09-11T11:59:00Z", "severity":"high",
              "resource":{"id":"orders-api","name":"Orders API"}, "rule":{"id":"5xx-rate","name":"5xx rate > 2%"},
              "environment":"production", "service":"orders", "summary":"5xx rate 4.1%", "dimensions":{"region":"weu"}, "url":"https://x" }
            """;
        var result = Run(BuiltInMappings.GenericWebhook, json);
        var e = result.Event;

        e.EventType.Should().Be(EventTypes.Firing);
        e.SourceAlertId.Should().Be("orders-api-5xx");
        e.SourceEventId.Should().Be("e-1");
        e.OccurredAt.Should().Be(new DateTimeOffset(2026, 9, 11, 11, 59, 0, TimeSpan.Zero));
        e.Severity.Should().Be(Severity.High);
        e.ResourceId.Should().Be("orders-api");
        e.RuleName.Should().Be("5xx rate > 2%");
        e.Environment.Should().Be("production");
        e.Dimensions.Should().Equal(new Dictionary<string, string> { ["region"] = "weu" });
        e.DeliveryKey.Should().Be("evt:e-1");
        e.IdentityConfidence.Should().Be(DeliveryKey.ConfidenceExact);
        e.Fingerprint.Should().MatchRegex("^[0-9a-f]{64}$");
        e.IdentityComponents.Should().Equal(
            new IdentityComponent("environment", "production"),
            new IdentityComponent("resource", "orders-api"),
            new IdentityComponent("rule", "5xx-rate"),
            new IdentityComponent("dims", "region=weu"));
        e.LifecycleProfileHint.Should().Be("unknown");
    }

    [Fact]
    public void Same_condition_yields_the_same_fingerprint_regardless_of_key_order_and_whitespace()
    {
        var a = Run(BuiltInMappings.GenericWebhook, """{"eventType":"firing","alertId":"a","rule":{"id":"r"},"environment":"prod","resource":{"id":"db-1 "},"dimensions":{"b":"2","a":"1"}}""");
        var b = Run(BuiltInMappings.GenericWebhook, """{"dimensions":{"a":"1","b":"2"},"resource":{"id":" db-1"},"environment":"prod","rule":{"id":"r"},"alertId":"a","eventType":"firing"}""");
        a.Event.Fingerprint.Should().Be(b.Event.Fingerprint);

        var c = Run(BuiltInMappings.GenericWebhook, """{"eventType":"firing","alertId":"a","rule":{"id":"r"},"environment":"prod","resource":{"id":"db-2"}}""");
        c.Event.Fingerprint.Should().NotBe(a.Event.Fingerprint);
    }

    [Fact]
    public void Missing_required_identity_component_is_a_mapping_exception_not_a_merge()
    {
        var doc = MappingParser.ParseYaml("""
            mapping:
              fields:
                event_type: { const: firing }
                source_alert_id: { path: "$.id" }
                rule_id: { path: "$.rule" }
              required: [event_type]
              identity: [ { name: rule, field: rule_id } ]
            """);
        var act = () => Run(doc, """{"id":"a"}""");
        act.Should().Throw<MappingException>().WithMessage("*'rule'*").Which.Field.Should().Be("rule");
    }

    [Fact]
    public void Unknown_event_type_and_missing_required_fields_are_quarantined()
    {
        var act1 = () => Run(BuiltInMappings.GenericWebhook, """{"eventType":"exploded","alertId":"a","rule":{"id":"r"}}""");
        act1.Should().Throw<MappingException>().Which.Field.Should().Be("event_type");
        var act2 = () => Run(BuiltInMappings.GenericWebhook, """{"eventType":"firing"}""");
        act2.Should().Throw<MappingException>().Which.Field.Should().Be("source_alert_id");
        var act3 = () => Run(BuiltInMappings.GenericWebhook, "not json");
        act3.Should().Throw<MappingException>().WithMessage("*not valid JSON*");
    }

    [Fact]
    public void Delivery_key_falls_back_through_the_four_strategies()
    {
        var doc = MappingParser.ParseYaml("""
            mapping:
              ignore_paths_for_body_key: ["$.sentAt"]
              fields:
                event_type: { const: firing }
                source_alert_id: { path: "$.id" }
                source_event_id: { path: "$.eid" }
                source_version: { path: "$.v", as: string }
                occurred_at: { path: "$.at", as: timestamp }
                rule_id: { const: r }
              required: [event_type]
              identity: [ { name: rule, field: rule_id } ]
            """);
        Run(doc, """{"id":"a","eid":"x","v":3,"at":"2026-01-01T00:00:00Z"}""").Event.DeliveryKey.Should().Be("evt:x");
        Run(doc, """{"id":"a","v":3,"at":"2026-01-01T00:00:00Z"}""").Event.DeliveryKey.Should().Be("ver:a:3");
        Run(doc, """{"id":"a","at":"2026-01-01T00:00:00.250Z"}""").Event.DeliveryKey.Should().Be("ts:a:firing:2026-01-01T00:00:00.250Z");

        var body1 = Run(doc, """{"x":1,"sentAt":"2026-01-01T00:00:00Z"}""").Event;
        var body2 = Run(doc, """{"sentAt":"2026-01-02T00:00:00Z", "x":1}""").Event;
        body1.DeliveryKey.Should().StartWith("body:").And.Be(body2.DeliveryKey, "ignored paths and key order do not change the body key");
        body1.IdentityConfidence.Should().Be(DeliveryKey.ConfidenceBody);
        Run(doc, """{"x":2}""").Event.DeliveryKey.Should().NotBe(body1.DeliveryKey);
    }

    [Fact]
    public void Azure_common_alert_schema_reference_mapping_from_07_works_end_to_end()
    {
        var doc = MappingParser.ParseYaml(AzureMappingYaml);
        var result = Run(doc, AzureFiredPayload);
        var e = result.Event;

        e.EventType.Should().Be("firing");
        e.SourceAlertId.Should().Be("/subscriptions/1111-2222/providers/Microsoft.AlertsManagement/alerts/abc");
        e.SourceEventId.Should().Be("origin-1");
        e.OccurredAt.Should().Be(new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero));
        e.Severity.Should().Be(Severity.High);
        e.Summary.Should().Be("cpu-high: CPU above 90%");
        e.ResourceId.Should().Be("/subscriptions/1111-2222/resourcegroups/rg-prod/providers/microsoft.compute/virtualmachines/vm1");
        e.RuleId.Should().Be("/subscriptions/1111-2222/providers/microsoft.insights/metricalerts/cpu-high");
        e.Environment.Should().Be("production");
        e.Service.Should().Be("marketplace");
        e.Dimensions.Should().Equal(new Dictionary<string, string> { ["metric"] = "Percentage CPU", ["ns"] = "Microsoft.Compute/virtualMachines" });
        e.Labels.Should().Equal(new Dictionary<string, string> { ["monitoringService"] = "Platform", ["signalType"] = "Metric" });
        e.RunbookUrl.Should().Be("https://runbooks/cpu");
        e.IdentityComponents![0].Should().Be(new IdentityComponent("scope", "/subscriptions/1111-2222"));
        e.LifecycleProfileHint.Should().Be("explicit_recovery");

        var resolved = Run(doc, AzureFiredPayload.Replace("\"Fired\"", "\"Resolved\"", StringComparison.Ordinal).Replace("\"resolvedDateTime\": null", "\"resolvedDateTime\": \"2026-09-11T10:30:00Z\"", StringComparison.Ordinal)).Event;
        resolved.EventType.Should().Be("resolved");
        resolved.OccurredAt.Should().Be(new DateTimeOffset(2026, 9, 11, 10, 30, 0, TimeSpan.Zero));
        resolved.Fingerprint.Should().Be(e.Fingerprint, "severity/summary/time never enter the fingerprint");
    }

    [Fact]
    public void Atlas_reference_mapping_from_07_works_including_jsonpath_filter_and_headers()
    {
        var doc = MappingParser.ParseYaml(AtlasMappingYaml);
        var json = """
            { "id":"al-1", "status":"OPEN", "created":"2026-09-11T09:00:00Z", "updated":"2026-09-11T09:05:00Z", "eventTypeName":"HOST_DOWN",
              "clusterName":"prod-cluster", "hostnameAndPort":"Shard-00-01.mongodb.net:27017", "alertConfigId":"cfg-9", "groupId":"proj-prod",
              "metricName": null, "replicaSetName":"rs0",
              "links":[ {"rel":"self","href":"https://api"}, {"rel":"http://mms.mongodb.com/alert","href":"https://cloud.mongodb.com/alert/1"} ] }
            """;
        var e = Run(doc, json, new Dictionary<string, string> { ["X-MMS-Event"] = "alert.open" }).Event;
        e.EventType.Should().Be("firing");
        e.SourceEventId.Should().Be("al-1:OPEN:2026-09-11T09:05:00Z");
        e.DeliveryKey.Should().Be("evt:al-1:OPEN:2026-09-11T09:05:00Z");
        e.Severity.Should().Be(Severity.Critical);
        e.ResourceId.Should().Be("shard-00-01.mongodb.net:27017");
        e.ResourceName.Should().Be("prod-cluster");
        e.Environment.Should().Be("production");
        e.SourceUrl.Should().Be("https://cloud.mongodb.com/alert/1");
        e.Dimensions.Should().Equal(new Dictionary<string, string> { ["replicaSet"] = "rs0" });
        e.Labels.Should().Equal(new Dictionary<string, string> { ["mmsEvent"] = "alert.open" });
        e.IdentityComponents!.Select(c => c.Name).Should().Equal("project", "environment", "resource", "rule", "dims");
    }

    [Fact]
    public void Applies_when_selects_a_mapping_and_first_nonempty_when_conditions_work()
    {
        var doc = MappingParser.ParseYaml("""
            mapping:
              applies_when: { eq: ["$.kind", "canary"] }
              fields:
                event_type: { const: heartbeat }
                source_alert_id: { path: "$.id" }
                rule_id: { const: canary }
              required: [event_type]
              identity: [ { name: rule, field: rule_id } ]
            """);
        MappingEngine.AppliesTo(doc, JsonNode.Parse("""{"kind":"canary"}"""), new JsonObject()).Should().BeTrue();
        MappingEngine.AppliesTo(doc, JsonNode.Parse("""{"kind":"alert"}"""), new JsonObject()).Should().BeFalse();
        var hb = Run(doc, """{"kind":"canary","id":"c1"}""").Event;
        hb.EventType.Should().Be(EventTypes.Heartbeat);
        hb.Fingerprint.Should().BeNull("heartbeat events carry no alert identity");
    }

    [Fact]
    public void Array_pick_object_lookup_regex_and_transforms_are_supported()
    {
        var doc = MappingParser.ParseYaml("""
            mapping:
              fields:
                event_type: { const: firing }
                source_alert_id: { const: a }
                rule_id: { const: r }
                summary: { array: { from: "$.items[*]", each: { template: "{$.name}={$.value}" }, join: ", " } }
                labels: { pick: [ "$.meta.team", "$.meta['cost-center']" ], as: object }
                resource_id: { path: "$.host", transform: [hostname, lower, "truncate:6"] }
                environment: { lookup: { from: "$.sub", regex_map: { "/subscriptions/1111-.*": production, "/subscriptions/2222-.*": staging }, default: unknown } }
                dimensions: { object: { region: { path: "$.region", transform: [upper] }, zone: { path: "$.zone", default: none } } }
                source_severity: { path: "$.sev", as: int }
              required: [event_type]
              identity: [ { name: rule, field: rule_id } ]
            """);
        var e = Run(doc, """{"items":[{"name":"a","value":1},{"name":"b","value":"x"}],"meta":{"team":"mpt","cost-center":"cc1"},"host":"Db-Host.Example.com:27017","sub":"/subscriptions/2222-abc","region":"weu","sev":"3"}""").Event;
        e.Summary.Should().Be("a=1, b=x");
        e.Labels.Should().Equal(new Dictionary<string, string> { ["team"] = "mpt", ["cost-center"] = "cc1" });
        e.ResourceId.Should().Be("db-hos");
        e.Environment.Should().Be("staging");
        e.Dimensions.Should().Equal(new Dictionary<string, string> { ["region"] = "WEU", ["zone"] = "none" });
        e.SourceSeverity.Should().Be("3");
    }

    [Fact]
    public void Parser_reports_every_error_with_its_path()
    {
        var act = () => MappingParser.ParseYaml("""
            mapping:
              fields:
                event_type: { path: "$.[" }
                bogus_field: { const: 1 }
                severity: { lookup: { from: "$.s" } }
                summary: { path: "$.a", const: b }
                rule_id: { path: "$.r", transform: [shout] }
              required: [event_type, resource_id]
              identity: []
              lifecycle_profile_hint: eternal
            """);
        var errors = act.Should().Throw<MappingValidationException>().Which.Errors.Select(e => e.ToString()).ToList();
        errors.Should().Contain(e => e.StartsWith("$.mapping.fields.event_type.path:", StringComparison.Ordinal) && e.Contains("invalid JSONPath", StringComparison.Ordinal));
        errors.Should().Contain(e => e.StartsWith("$.mapping.fields.bogus_field:", StringComparison.Ordinal));
        errors.Should().Contain(e => e.StartsWith("$.mapping.fields.severity.lookup:", StringComparison.Ordinal));
        errors.Should().Contain(e => e.StartsWith("$.mapping.fields.summary:", StringComparison.Ordinal) && e.Contains("several kinds", StringComparison.Ordinal));
        errors.Should().Contain(e => e.Contains("unknown transform 'shout'", StringComparison.Ordinal));
        errors.Should().Contain(e => e.Contains("required field 'resource_id' has no rule", StringComparison.Ordinal));
        errors.Should().Contain(e => e.StartsWith("$.mapping.identity:", StringComparison.Ordinal));
        errors.Should().Contain(e => e.Contains("unknown profile 'eternal'", StringComparison.Ordinal));
    }

    [Fact]
    public void Predicates_compare_severity_on_the_canonical_ordinal()
    {
        var errors = new List<MappingValidationError>();
        var p = PredicateParser.Parse(YamlJson.Parse("{ all: [ { gte: [severity, high] }, { in: [service, [orders, billing]] }, { not: { eq: [environment, staging] } } ] }"), "$", errors)!;
        errors.Should().BeEmpty();
        JsonNode? Resolve(string r) => r switch
        {
            "severity" => JsonValue.Create("unknown"),
            "service" => JsonValue.Create("orders"),
            "environment" => JsonValue.Create("production"),
            _ => null,
        };
        PredicateEvaluator.Evaluate(p, Resolve).Should().BeTrue("unknown ranks as high");
        PredicateEvaluator.Evaluate(p, r => r == "severity" ? JsonValue.Create("medium") : Resolve(r)).Should().BeFalse();
    }

    [Fact]
    public void Yaml_round_trips_through_json()
    {
        var node = YamlJson.Parse("a: 1\nb: 'two'\nc: [true, null, 2.5]\nd: { e: text }\n")!;
        node["a"]!.GetValue<long>().Should().Be(1);
        node["b"]!.GetValue<string>().Should().Be("two");
        node["c"]![2]!.GetValue<double>().Should().Be(2.5);
        var yaml = YamlJson.ToYaml(node);
        var again = YamlJson.Parse(yaml);
        JsonNode.DeepEquals(node, again).Should().BeTrue();
    }

    private const string AzureMappingYaml = """
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
            environment:      { lookup: { from: "$.data.essentials.alertTargetIDs[0]", regex_map: { "/subscriptions/1111-.*": production, "/subscriptions/2222-.*": staging }, default: unknown } }
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
          lookup_tables:
            runbooks: { "/subscriptions/1111-2222/providers/microsoft.insights/metricalerts/cpu-high": "https://runbooks/cpu" }
            resource_service: { "/subscriptions/1111-2222/resourcegroups/rg-prod/providers/microsoft.compute/virtualmachines/vm1": marketplace }
          enrich:
            - { set: runbook_url, lookup_table: runbooks, key: rule_id }
            - { set: service,     lookup_table: resource_service, key: resource_id, when_empty: true }
          lifecycle_profile_hint: explicit_recovery
        """;

    private const string AzureFiredPayload = """
        { "schemaId": "azureMonitorCommonAlertSchema", "id": "evt-1",
          "data": {
            "essentials": {
              "alertId": "/subscriptions/1111-2222/providers/Microsoft.AlertsManagement/alerts/abc",
              "alertRule": "cpu-high", "alertRuleID": "/subscriptions/1111-2222/providers/Microsoft.Insights/metricAlerts/cpu-high",
              "severity": "Sev1", "signalType": "Metric", "monitorCondition": "Fired", "monitoringService": "Platform",
              "alertTargetIDs": ["/subscriptions/1111-2222/resourceGroups/rg-prod/providers/Microsoft.Compute/virtualMachines/vm1"],
              "configurationItems": ["vm1"], "originAlertId": "origin-1",
              "firedDateTime": "2026-09-11T10:00:00Z", "resolvedDateTime": null,
              "description": "CPU above 90%", "essentialsVersion": "1.0", "alertContextVersion": "1.0",
              "investigationLink": "https://portal/alert/abc"
            },
            "alertContext": { "condition": { "allOf": [ { "metricName": "Percentage CPU", "metricNamespace": "Microsoft.Compute/virtualMachines", "dimensions": [] } ] } },
            "customProperties": {}
          } }
        """;

    private const string AtlasMappingYaml = """
        mapping:
          identity_version: 1
          fields:
            event_type:
              lookup: { from: "$.status", map: { OPEN: firing, TRACKING: update, CLOSED: resolved, CANCELLED: cancelled }, default: unknown }
            source_alert_id:  { path: "$.id" }
            source_event_id:  { template: "{$.id}:{$.status}:{$.updated}" }
            occurred_at:      { first_nonempty: [ { path: "$.resolved" }, { path: "$.updated" }, { path: "$.created" } ], as: timestamp }
            severity:
              lookup: { from: "$.eventTypeName", map: { HOST_DOWN: critical, REPLICATION_OPLOG_WINDOW_RUNNING_OUT: high, OUTSIDE_METRIC_THRESHOLD: medium, CREDIT_CARD_ABOUT_TO_EXPIRE: informational }, default: unknown }
            summary:          { template: "{$.eventTypeName} on {$.clusterName} {$.hostnameAndPort}" }
            resource_id:      { first_nonempty: [ { path: "$.hostnameAndPort", transform: [lower] }, { path: "$.clusterName", transform: [lower] } ] }
            resource_name:    { first_nonempty: [ { path: "$.clusterName" }, { path: "$.hostnameAndPort" } ] }
            rule_id:          { path: "$.alertConfigId" }
            rule_name:        { path: "$.eventTypeName" }
            environment:      { lookup: { from: "$.groupId", map: { "proj-prod": production, "proj-staging": staging }, default: unknown } }
            dimensions:       { object: { metric: { path: "$.metricName" }, replicaSet: { path: "$.replicaSetName" } } }
            labels:           { object: { mmsEvent: { header: "X-MMS-Event" } } }
            source_url:       { path: "$.links[?(@.rel=='http://mms.mongodb.com/alert')].href" }
          required: [event_type, source_alert_id, rule_id]
          identity:
            - { name: project,  path: "$.groupId" }
            - { name: environment, field: environment }
            - { name: resource, field: resource_id }
            - { name: rule,     field: rule_id }
            - { name: dims,     field: dimensions, optional: true }
          lifecycle_profile_hint: queryable_state
        """;
}
