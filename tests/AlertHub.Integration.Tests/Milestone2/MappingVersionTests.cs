using System.Text;
using System.Text.Json.Nodes;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Ingest;
using AlertHub.Application.Integrations;
using AlertHub.Application.Mapping;
using AlertHub.Application.Processing;
using AlertHub.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlertHub.Integration.Tests.Milestone2;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MappingVersionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private string _cs = string.Empty;
    private ServiceProvider _services = null!;
    private IntegrationCredentials _integration = null!;

    public async Task InitializeAsync()
    {
        _cs = await postgres.CreateDatabaseAsync("mappings");
        _services = TestServices.Build(_cs);
        _integration = await TestServices.CreateIntegrationAsync(_services, "azure", "azure_monitor", "scope-a");
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    private const string Yaml = """
        mapping:
          fields:
            event_type: { lookup: { from: "$.state", map: { on: firing, off: resolved } } }
            source_alert_id: { path: "$.id" }
            rule_id: { path: "$.rule" }
            severity: { const: high }
          required: [event_type, source_alert_id, rule_id]
          identity: [ { name: rule, field: rule_id } ]
        """;

    private static readonly MappingSample GoodSample = new("on", JsonNode.Parse("""{"state":"on","id":"1","rule":"r"}""")!, null, new Dictionary<string, string> { ["event_type"] = "firing", ["severity"] = "high" });

    [Fact]
    public async Task Create_version_is_inactive_until_activated_and_activation_switches_the_active_version()
    {
        var actor = Actor.System("test");
        var v1 = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<MappingService>()
            .CreateVersionAsync(new CreateMappingVersion(_integration.Integration.IntegrationId, Yaml, Samples: [GoodSample]), actor));
        v1.Version.Should().Be(1);
        v1.IsActive.Should().BeFalse();

        // Without an active mapping, an azure_monitor integration has nothing to interpret with → quarantine.
        var payload = await IngestAsync("""{"state":"on","id":"1","rule":"r"}""");
        (await ProcessAsync(payload)).Outcome.Should().Be(ProcessingOutcome.MappingFailed);

        await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<MappingService>().ActivateAsync(v1.MappingId, 1, actor));
        var opened = await ProcessAsync(await IngestAsync("""{"state":"on","id":"2","rule":"r"}"""));
        opened.Outcome.Should().Be(ProcessingOutcome.Opened);

        var v2 = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<MappingService>()
            .CreateVersionAsync(new CreateMappingVersion(_integration.Integration.IntegrationId, Yaml.Replace("const: high", "const: critical", StringComparison.Ordinal), v1.MappingId), actor));
        v2.Version.Should().Be(2);
        await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<MappingService>().ActivateAsync(v1.MappingId, 2, actor));

        await using var db = PostgresFixture.CreateContext(_cs);
        var versions = await db.MappingVersions.Where(m => m.MappingId == v1.MappingId).OrderBy(m => m.Version).ToListAsync();
        versions.Select(m => m.IsActive).Should().Equal(false, true);
        (await db.AuditEntries.CountAsync(a => a.Action == "mapping.activate")).Should().Be(2);
    }

    [Fact]
    public async Task Activation_is_refused_when_a_sample_no_longer_passes()
    {
        var actor = Actor.System("test");
        var badSample = new MappingSample("expects critical", GoodSample.Body, null, new Dictionary<string, string> { ["severity"] = "critical" });
        var act = () => TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<MappingService>()
            .CreateVersionAsync(new CreateMappingVersion(_integration.Integration.IntegrationId, Yaml, Samples: [badSample]), actor));
        (await act.Should().ThrowAsync<MappingValidationException>()).Which.Errors.Should().ContainSingle(e => e.Message.Contains("expected 'critical'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Invalid_yaml_is_rejected_with_paths()
    {
        var act = () => TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<MappingService>()
            .CreateVersionAsync(new CreateMappingVersion(_integration.Integration.IntegrationId, "mapping:\n  fields:\n    nope: { const: 1 }\n  identity: []\n"), Actor.System("test")));
        (await act.Should().ThrowAsync<MappingValidationException>()).Which.Errors.Should().Contain(e => e.Path == "$.mapping.fields.nope");
    }

    private async Task<NormaliseJobPayload> IngestAsync(string body)
    {
        var accepted = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<IngestService>()
            .AcceptAsync(_integration.Integration, new IngestRequest(Encoding.UTF8.GetBytes(body), "application/json", new Dictionary<string, string>(), null)));
        return new NormaliseJobPayload(accepted.EventId, accepted.ReceivedAt, _integration.Integration.IntegrationId);
    }

    private Task<ProcessingResult> ProcessAsync(NormaliseJobPayload payload)
        => TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<EventProcessor>().ProcessAsync(payload));
}
