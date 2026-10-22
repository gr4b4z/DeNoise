using System.Text;
using System.Text.Json.Nodes;
using DeNoise.Application.Mapping;
using DeNoise.Domain.Alerts;

namespace DeNoise.Application.Tests.Mapping;

/// <summary>
/// Fields may reference each other in <c>when</c>, <c>lookup.from</c> and templates regardless of their order in the document —
/// jsonb storage does not keep key order, so a mapping that worked in the editor must work after activation (milestone 16).
/// </summary>
[Trait("Category", "Unit")]
public sealed class FieldOrderTests
{
    private const string Yaml = """
        mapping:
          identity_version: 1
          fields:
            rule_id:    { lookup: { from: labels.kind, map: { build: build-failed, deploy: deploy-failed } }, when: { in: [event_type, [firing, resolved]] } }
            summary:    { template: "{labels.kind} {$.name} {event_type}" }
            event_type: { lookup: { from: "$.result", map: { failed: firing, ok: resolved }, default: informational } }
            severity:   { first_nonempty: [ { const: high, when: { eq: [labels.kind, deploy] } }, { const: low } ] }
            source_alert_id: { path: "$.id" }
            labels:
              object:
                kind: { lookup: { from: "$.name", regex_map: { "deploy-.*": deploy }, default: build } }
          required: [event_type, source_alert_id]
          identity:
            - { name: rule, field: rule_id }
        """;

    private static readonly Guid IntegrationId = Guid.Parse("00000000-0000-0000-0000-00000000f0f0");

    private static MappingResult Run(JsonNode root, string payload)
    {
        var doc = MappingParser.Parse(root, 1);
        return MappingEngine.Normalise(doc, new RawInput(Encoding.UTF8.GetBytes(payload), new Dictionary<string, string>()), new MappingContext(IntegrationId, Guid.NewGuid(), DateTimeOffset.UnixEpoch, 1));
    }

    [Fact]
    public void References_to_later_fields_resolve_in_document_order_and_in_jsonb_order()
    {
        var root = YamlJson.Parse(Yaml)!;
        var ordered = Run(root, """{"id":"r1","name":"deploy-prod","result":"failed"}""");
        ordered.Event.EventType.Should().Be(EventTypes.Firing);
        ordered.Event.RuleId.Should().Be("deploy-failed");
        ordered.Event.Summary.Should().Be("deploy deploy-prod firing");
        ordered.Event.Severity.Should().Be(Domain.Common.Severity.High);

        // jsonb orders keys by length, then bytes: event_type, labels, rule_id, severity, source_alert_id, summary become e.g. labels, rule_id, summary, severity, event_type…
        var shuffled = JsonNode.Parse(root.ToJsonString())!.AsObject();
        var fields = shuffled["mapping"]!["fields"]!.AsObject();
        var reordered = new JsonObject();
        foreach (var key in fields.Select(kv => kv.Key).OrderBy(k => k.Length).ThenBy(k => k, StringComparer.Ordinal).Reverse().ToList())
        {
            reordered[key] = fields[key]!.DeepClone();
        }
        shuffled["mapping"]!["fields"] = reordered;
        var result = Run(shuffled, """{"id":"r1","name":"deploy-prod","result":"failed"}""");
        result.Event.RuleId.Should().Be("deploy-failed");
        result.Event.Summary.Should().Be("deploy deploy-prod firing");
        result.Event.Severity.Should().Be(Domain.Common.Severity.High);
        result.Event.Fingerprint.Should().Be(ordered.Event.Fingerprint);
    }

    [Fact]
    public void A_cyclic_reference_resolves_to_empty_instead_of_recursing()
    {
        var yaml = """
            mapping:
              identity_version: 1
              fields:
                event_type: { const: informational }
                source_alert_id: { path: "$.id" }
                rule_id:   { template: "a-{rule_name}" }
                rule_name: { template: "b-{rule_id}" }
              required: [event_type, source_alert_id]
              identity:
                - { name: rule, field: rule_id }
            """;
        var result = Run(YamlJson.Parse(yaml)!, """{"id":"r1"}""");
        result.Event.RuleId.Should().Be("a-b-", "the inner reference to the field being evaluated is empty");
        result.Event.RuleName.Should().Be("b-", "rule_name was evaluated on demand while rule_id was in progress and is memoised");
    }
}
