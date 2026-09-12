using System.Text;
using System.Text.Json.Nodes;
using AlertHub.Application.Mapping;
using AlertHub.Domain.Alerts;
using AlertHub.Domain.Common;
using AlertHub.Domain.Episodes;
using AlertHub.Domain.Integrations;

namespace AlertHub.Application.Tests.Mapping;

/// <summary>07 §2–3 reference mappings: parse, pass their own samples, select by <c>applies_when</c>, and produce the canonical fields.</summary>
[Trait("Category", "Unit")]
public sealed class ReferenceMappingsTests
{
    private static readonly Guid IntegrationId = Guid.Parse("00000000-0000-0000-0000-00000000a8a8");

    private static MappingResult Run(string yaml, string payload, IReadOnlyDictionary<string, string>? headers = null)
    {
        var doc = MappingParser.ParseYaml(yaml, 1);
        return MappingEngine.Normalise(doc, new RawInput(Encoding.UTF8.GetBytes(payload), headers ?? new Dictionary<string, string>()), new MappingContext(IntegrationId, Guid.NewGuid(), DateTimeOffset.UnixEpoch, 1));
    }

    [Theory]
    [InlineData(IntegrationTypes.AzureMonitor, 4)]
    [InlineData(IntegrationTypes.Atlas, 1)]
    [InlineData(IntegrationTypes.GenericWebhook, 0)]
    public void Every_reference_mapping_parses_and_passes_its_samples(string type, int expectedCount)
    {
        var refs = ReferenceMappings.For(type);
        refs.Should().HaveCount(expectedCount);
        foreach (var r in refs)
        {
            var doc = MappingParser.ParseYaml(r.Yaml, 1);
            MappingService.VerifySamples(doc, r.Samples, IntegrationId).Should().BeEmpty($"{r.Name} must pass its own samples (activation would be refused otherwise)");
        }
        refs.Select(r => r.Order).Should().BeInAscendingOrder("evaluation order: canary and overrides before the common schema");
    }

    [Fact]
    public void Azure_fired_and_resolved_share_the_fingerprint_and_the_explicit_recovery_hint()
    {
        var fired = Run(ReferenceMappings.AzureMonitorYaml, ReferenceMappings.AzureFiredSample);
        var resolved = Run(ReferenceMappings.AzureMonitorYaml, ReferenceMappings.AzureResolvedSample);
        fired.Event.EventType.Should().Be(EventTypes.Firing);
        fired.Event.Severity.Should().Be(Severity.High);
        fired.Event.RuleId.Should().Be("/subscriptions/00000000-0000-0000-0000-000000000001/resourcegroups/rg-prod/providers/microsoft.insights/metricalerts/cpu-high");
        fired.Event.Summary.Should().Be("cpu-high: CPU above 90%");
        fired.Event.LifecycleProfileHint.Should().Be("explicit_recovery");
        fired.Event.IdentityComponents.Should().Contain(c => c.Name == "scope" && c.Value == "/subscriptions/00000000-0000-0000-0000-000000000001");
        resolved.Event.EventType.Should().Be(EventTypes.Resolved);
        resolved.Event.OccurredAt.Should().Be(new DateTimeOffset(2026, 9, 11, 10, 20, 0, TimeSpan.Zero), "resolved events take resolvedDateTime");
        resolved.Event.Fingerprint.Should().Be(fired.Event.Fingerprint, "Fired and Resolved of one rule on one resource are one condition");
        resolved.Event.DeliveryKey.Should().NotBe(fired.Event.DeliveryKey);
    }

    [Fact]
    public void Canary_rules_are_selected_by_applies_when_and_become_heartbeat_signals()
    {
        var canary = MappingParser.ParseYaml(ReferenceMappings.AzureCanaryYaml, 1);
        var common = MappingParser.ParseYaml(ReferenceMappings.AzureMonitorYaml, 1);
        var canaryBody = JsonNode.Parse(ReferenceMappings.AzureCanarySample);
        var firedBody = JsonNode.Parse(ReferenceMappings.AzureFiredSample);
        MappingEngine.AppliesTo(canary, canaryBody, new JsonObject()).Should().BeTrue();
        MappingEngine.AppliesTo(canary, firedBody, new JsonObject()).Should().BeFalse("only alerthub-canary-* rules");
        MappingEngine.AppliesTo(common, firedBody, new JsonObject()).Should().BeTrue("the common schema has no applies_when");

        var signal = Run(ReferenceMappings.AzureCanaryYaml, ReferenceMappings.AzureCanarySample);
        signal.Event.EventType.Should().Be(EventTypes.Heartbeat);
        signal.Event.Labels.Should().ContainKey("canary");
        signal.Event.LifecycleProfileHint.Should().Be("one_shot");
    }

    [Fact]
    public void Resource_health_available_is_a_recovery_and_service_health_is_informational()
    {
        var unavailable = ReferenceMappings.AzureFiredSample
            .Replace("\"monitoringService\": \"Platform\"", "\"monitoringService\": \"Resource Health\"", StringComparison.Ordinal)
            .Replace("\"alertContext\": { \"condition\"", "\"alertContext\": { \"properties\": { \"currentHealthStatus\": \"Unavailable\", \"cause\": \"PlatformInitiated\" }, \"condition\"", StringComparison.Ordinal);
        var available = unavailable.Replace("\"Unavailable\"", "\"Available\"", StringComparison.Ordinal);
        var health = MappingParser.ParseYaml(ReferenceMappings.AzureResourceHealthYaml, 1);
        MappingEngine.AppliesTo(health, JsonNode.Parse(unavailable), new JsonObject()).Should().BeTrue();
        Run(ReferenceMappings.AzureResourceHealthYaml, unavailable).Event.EventType.Should().Be(EventTypes.Firing);
        Run(ReferenceMappings.AzureResourceHealthYaml, available).Event.EventType.Should().Be(EventTypes.Resolved);

        var serviceHealth = ReferenceMappings.AzureFiredSample
            .Replace("\"monitoringService\": \"Platform\"", "\"monitoringService\": \"ServiceHealth\"", StringComparison.Ordinal)
            .Replace("\"alertContext\": { \"condition\"", "\"alertContext\": { \"properties\": { \"title\": \"Storage incident\", \"incidentType\": \"Incident\", \"trackingId\": \"XYZ-123\" }, \"condition\"", StringComparison.Ordinal);
        var sh = Run(ReferenceMappings.AzureServiceHealthYaml, serviceHealth);
        sh.Event.EventType.Should().Be(EventTypes.Informational);
        sh.Event.Summary.Should().Be("cpu-high: Storage incident");
        sh.Event.IdentityComponents.Should().Contain(c => c.Name == "event" && c.Value == "XYZ-123");
    }

    [Fact]
    public void Atlas_status_transitions_map_to_event_types_on_one_fingerprint_with_header_fallback()
    {
        var open = Run(ReferenceMappings.AtlasYaml, ReferenceMappings.AtlasOpenSample);
        open.Event.EventType.Should().Be(EventTypes.Firing);
        open.Event.Severity.Should().Be(Severity.Medium, "OUTSIDE_METRIC_THRESHOLD → medium in the seeded map");
        open.Event.ResourceId.Should().Be("orders-prod-shard-00-01.abcde.mongodb.net:27017");
        open.Event.RuleId.Should().Be("5f1234567890abcdef654321");
        open.Event.SourceUrl.Should().StartWith("https://cloud.mongodb.com/v2/");
        open.Event.LifecycleProfileHint.Should().Be("queryable_state");

        var closed = Run(ReferenceMappings.AtlasYaml, ReferenceMappings.AtlasOpenSample.Replace("\"status\": \"OPEN\"", "\"status\": \"CLOSED\"", StringComparison.Ordinal).Replace("\"updated\": \"2026-09-11T10:00:00Z\"", "\"updated\": \"2026-09-11T10:30:00Z\", \"resolved\": \"2026-09-11T10:30:00Z\"", StringComparison.Ordinal));
        closed.Event.EventType.Should().Be(EventTypes.Resolved);
        closed.Event.Fingerprint.Should().Be(open.Event.Fingerprint);
        closed.Event.SourceEventId.Should().NotBe(open.Event.SourceEventId, "Atlas has no event id: id+status+updated stands in");

        var noStatus = ReferenceMappings.AtlasOpenSample.Replace("\"status\": \"OPEN\", ", string.Empty, StringComparison.Ordinal);
        Run(ReferenceMappings.AtlasYaml, noStatus, new Dictionary<string, string> { ["X-MMS-Event"] = "alert.close" }).Event.EventType.Should().Be(EventTypes.Resolved, "$headers fallback");
    }
}
