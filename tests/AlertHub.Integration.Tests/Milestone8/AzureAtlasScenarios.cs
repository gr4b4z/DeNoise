using System.Text;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Ingest;
using AlertHub.Application.Integrations;
using AlertHub.Application.Mapping;
using AlertHub.Application.Processing;
using AlertHub.Domain.Alerts;
using AlertHub.Domain.Common;
using AlertHub.Domain.Episodes;
using AlertHub.Domain.Integrations;
using AlertHub.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AlertHub.Integration.Tests.Milestone8;

/// <summary>
/// Milestone 8: an Azure Monitor and an Atlas integration work from their seeded reference mappings — the explicit-recovery
/// style flows of 07 §2 (Fired opens, Resolved closes as source-confirmed), the canary rule feeds coverage instead of opening
/// an episode, and Atlas OPEN/CLOSED share one condition.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AzureAtlasScenarios(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    private string _cs = string.Empty;
    private FakeTimeProvider _time = null!;
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        _cs = await postgres.CreateDatabaseAsync("azureatlas");
        _time = new FakeTimeProvider(T0);
        _services = TestServices.Build(_cs, _time);
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    private async Task<IntegrationCredentials> CreateSeededAsync(string type, string coverage = "{}")
    {
        var actor = Actor.System("setup");
        return await TestServices.InScopeAsync(_services, async sp =>
        {
            var created = await sp.GetRequiredService<IntegrationService>().CreateAsync(new CreateIntegration(type, type, "scope-a", null, Coverage: coverage), actor);
            (await sp.GetRequiredService<MappingService>().SeedReferenceAsync(created.Integration, actor)).Should().Be(ReferenceMappings.For(type).Count);
            (await sp.GetRequiredService<MappingService>().SeedReferenceAsync(created.Integration, actor)).Should().Be(0, "seeding is idempotent");
            return created;
        });
    }

    private async Task<ProcessingResult> IngestAndProcessAsync(IntegrationCredentials integration, string body, IReadOnlyDictionary<string, string>? headers = null)
    {
        var accepted = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<IngestService>()
            .AcceptAsync(integration.Integration, new IngestRequest(Encoding.UTF8.GetBytes(body), "application/json", headers ?? new Dictionary<string, string>(), null)));
        return await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<EventProcessor>().ProcessAsync(new NormaliseJobPayload(accepted.EventId, accepted.ReceivedAt, integration.Integration.IntegrationId)));
    }

    [Fact]
    public async Task Scenario_ExplicitRecoveryAzureFlow_fired_opens_and_resolved_closes_source_confirmed()
    {
        var azure = await CreateSeededAsync(IntegrationTypes.AzureMonitor);
        var opened = await IngestAndProcessAsync(azure, ReferenceMappings.AzureFiredSample);
        opened.Outcome.Should().Be(ProcessingOutcome.Opened, opened.Detail);

        await using (var db = PostgresFixture.CreateContext(_cs))
        {
            var episode = await db.Episodes.AsNoTracking().SingleAsync(e => e.EpisodeId == opened.EpisodeId);
            episode.Severity.Should().Be(Severity.High);
            episode.LifecycleProfile.Should().Be("explicit_recovery", "the reference mapping's hint drives the lifecycle preset (spec §12.2)");
            episode.Summary.Should().Be("cpu-high: CPU above 90%");
            episode.SourceUrl.Should().Be("https://portal.azure.com/#view/alert/1");
        }

        _time.Advance(TimeSpan.FromMinutes(20));
        var resolved = await IngestAndProcessAsync(azure, ReferenceMappings.AzureResolvedSample);
        resolved.Outcome.Should().Be(ProcessingOutcome.Resolved, resolved.Detail);
        resolved.EpisodeId.Should().Be(opened.EpisodeId, "Fired and Resolved are one condition");
        await using (var db = PostgresFixture.CreateContext(_cs))
        {
            var episode = await db.Episodes.AsNoTracking().SingleAsync(e => e.EpisodeId == opened.EpisodeId);
            episode.HandlingState.Should().Be(HandlingState.Closed);
            episode.ClosureReason.Should().Be(ClosureReason.SourceResolved);
            episode.ResolutionEvidence.Should().Be(Evidence.Source, "a resolved webhook is source-confirmed evidence");
            (await db.Episodes.CountAsync()).Should().Be(1);
        }
    }

    [Fact]
    public async Task Canary_rule_payload_is_a_coverage_signal_not_an_episode()
    {
        var coverage = """{ "methods": [ { "type": "managed_canary", "expected_interval": "00:05:00", "delayed_after": "00:10:00", "alert_after": "00:15:00", "unavailable_after": "01:00:00", "recovery_successes_required": 2 } ] }""";
        var azure = await CreateSeededAsync(IntegrationTypes.AzureMonitor, coverage);
        var first = await IngestAndProcessAsync(azure, ReferenceMappings.AzureCanarySample);
        first.Outcome.Should().Be(ProcessingOutcome.CoverageSignal, first.Detail);
        var second = await IngestAndProcessAsync(azure, ReferenceMappings.AzureCanarySample.Replace("PROVISIONAL-canary-origin-1", "PROVISIONAL-canary-origin-2", StringComparison.Ordinal).Replace("PROVISIONAL-canary-1", "PROVISIONAL-canary-2", StringComparison.Ordinal));
        second.Outcome.Should().Be(ProcessingOutcome.CoverageSignal, second.Detail);

        await using var db = PostgresFixture.CreateContext(_cs);
        (await db.Episodes.CountAsync()).Should().Be(0, "canary payloads never open episodes (04 §2.1)");
        var state = await db.CoverageStates.AsNoTracking().SingleAsync(c => c.IntegrationId == azure.Integration.IntegrationId);
        state.LastSignalAt.Should().Be(_time.GetUtcNow());
        state.State.Should().Be(Domain.Ops.CoverageStates.Healthy, "two canary signals establish coverage");
        (await db.NormalisedEvents.CountAsync(e => e.EventType == EventTypes.Heartbeat)).Should().Be(2);
    }

    [Fact]
    public async Task Atlas_open_and_closed_are_one_condition_and_the_header_event_is_kept_as_a_label()
    {
        var atlas = await CreateSeededAsync(IntegrationTypes.Atlas);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-MMS-Event"] = "alert.open" };
        var opened = await IngestAndProcessAsync(atlas, ReferenceMappings.AtlasOpenSample, headers);
        opened.Outcome.Should().Be(ProcessingOutcome.Opened, opened.Detail);
        await using (var db = PostgresFixture.CreateContext(_cs))
        {
            var episode = await db.Episodes.AsNoTracking().SingleAsync(e => e.EpisodeId == opened.EpisodeId);
            episode.Severity.Should().Be(Severity.Medium);
            episode.LifecycleProfile.Should().Be("queryable_state");
            episode.ResourceName.Should().Be("orders-prod");
            var evt = await db.NormalisedEvents.AsNoTracking().SingleAsync();
            evt.Labels.Should().ContainKey("mmsEvent").WhoseValue.Should().Be("alert.open");
        }

        _time.Advance(TimeSpan.FromMinutes(30));
        var closedBody = ReferenceMappings.AtlasOpenSample
            .Replace("\"status\": \"OPEN\"", "\"status\": \"CLOSED\"", StringComparison.Ordinal)
            .Replace("\"updated\": \"2026-09-11T10:00:00Z\"", "\"updated\": \"2026-09-11T10:30:00Z\", \"resolved\": \"2026-09-11T10:30:00Z\"", StringComparison.Ordinal);
        var closed = await IngestAndProcessAsync(atlas, closedBody, new Dictionary<string, string> { ["X-MMS-Event"] = "alert.close" });
        closed.Outcome.Should().Be(ProcessingOutcome.Resolved, closed.Detail);
        closed.EpisodeId.Should().Be(opened.EpisodeId);
    }

    [Fact]
    public async Task Unmapped_payload_of_a_seeded_integration_is_quarantined_with_the_field_named()
    {
        var azure = await CreateSeededAsync(IntegrationTypes.AzureMonitor);
        var result = await IngestAndProcessAsync(azure, """{ "schemaId": "azureMonitorCommonAlertSchema", "data": { "essentials": { "alertRule": "x" } } }""");
        result.Outcome.Should().Be(ProcessingOutcome.MappingFailed);
        await using var db = PostgresFixture.CreateContext(_cs);
        var failure = await db.MappingFailures.AsNoTracking().SingleAsync();
        failure.Quarantined.Should().BeTrue();
        failure.Field.Should().NotBeNullOrEmpty();
        failure.MappingVersion.Should().Be(1, "the seeded common schema version was applied");
    }
}
