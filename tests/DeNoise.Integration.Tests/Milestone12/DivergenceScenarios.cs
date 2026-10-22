using System.Text;
using DeNoise.Application.Divergence;
using DeNoise.Application.Ingest;
using DeNoise.Application.Integrations;
using DeNoise.Application.Processing;
using DeNoise.Domain.Episodes;
using DeNoise.Infrastructure.Integrations;
using DeNoise.Infrastructure.Persistence;
using DeNoise.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DeNoise.Integration.Tests.Milestone12;

/// <summary>09 M12 / spec §21: shadow integrations ingest and process but never notify; the divergence report compares open episodes with the source's answer.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DivergenceScenarios(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    private FakeTimeProvider _time = null!;
    private ServiceProvider _services = null!;
    private IntegrationCredentials _shadow = null!;
    private readonly ScriptedAdapter _adapter = new();

    /// <summary>Answers per source alert id; anything else is "unsupported" (like a source without a state API).</summary>
    private sealed class ScriptedAdapter : IStateQueryAdapter
    {
        public Dictionary<string, StateQueryResult> Answers { get; } = new(StringComparer.Ordinal);
        public bool Supported { get; set; } = true;
        public Task<ProbeResult> ProbeAsync(Domain.Integrations.Integration integration, CancellationToken ct = default)
            => Task.FromResult(new ProbeResult(Supported, Supported, Supported ? null : "no state api"));
        public Task<StateQueryResult> QueryAsync(Domain.Integrations.Integration integration, Episode episode, CancellationToken ct = default)
            => Task.FromResult(!Supported ? new StateQueryResult(StateQueryOutcome.Unsupported, "no state api") : Answers.TryGetValue(episode.SourceAlertId ?? "", out var r) ? r : new StateQueryResult(StateQueryOutcome.Error, "timeout"));
    }

    public async Task InitializeAsync()
    {
        var cs = await postgres.CreateDatabaseAsync("divergence");
        _time = new FakeTimeProvider(T0);
        _services = TestServices.Build(cs, _time, configure: s => s.AddSingleton<IStateQueryAdapter>(_adapter), settings: new Dictionary<string, string?> { ["Pilot:DivergenceThreshold"] = "0.25" });
        _shadow = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<IntegrationService>().CreateAsync(new CreateIntegration("shadow-src", "generic_webhook", "scope-a"), Application.Abstractions.Actor.System("setup")));
        await TestServices.InScopeAsync(_services, async sp =>
        {
            await sp.GetRequiredService<IntegrationService>().UpdateAsync(_shadow.Integration.IntegrationId, _shadow.Integration.Version, new UpdateIntegration(Shadow: true), Application.Abstractions.Actor.System("setup"));
        });
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    private async Task<Guid> OpenAsync(string alertId, string severity = "high")
    {
        var body = $$"""{"eventType":"firing","alertId":"{{alertId}}","eventId":"{{Guid.NewGuid()}}","occurredAt":"{{_time.GetUtcNow():O}}","severity":"{{severity}}","environment":"production","service":"orders","resource":{"id":"res-{{alertId}}","name":"Orders"},"rule":{"id":"5xx","name":"5xx rate"},"summary":"5xx for {{alertId}}"}""";
        return await TestServices.InScopeAsync(_services, async sp =>
        {
            var integration = (await sp.GetRequiredService<IIntegrationRepository>().GetCurrentAsync(_shadow.Integration.IntegrationId))!;
            var accepted = await sp.GetRequiredService<IngestService>().AcceptAsync(integration, new IngestRequest(Encoding.UTF8.GetBytes(body), "application/json", new Dictionary<string, string>(), null));
            var result = await sp.GetRequiredService<EventProcessor>().ProcessAsync(new NormaliseJobPayload(accepted.EventId, accepted.ReceivedAt, integration.IntegrationId));
            return result.EpisodeId ?? throw new InvalidOperationException(result.Outcome.ToString());
        });
    }

    [Fact]
    public async Task Shadow_integration_processes_but_never_notifies_and_the_divergence_report_flags_source_resolved_episodes()
    {
        var a = await OpenAsync("a");
        var b = await OpenAsync("b");
        var c = await OpenAsync("c");
        var d = await OpenAsync("d");
        (await TestServices.InDbAsync(_services, db => db.Episodes.CountAsync(e => e.HandlingState != HandlingState.Closed))).Should().Be(4, "shadow still ingests and processes");
        (await TestServices.InDbAsync(_services, db => db.Outbox.CountAsync())).Should().Be(0, "shadow never stages notifications (no routing failure rows either)");

        _adapter.Answers["a"] = new StateQueryResult(StateQueryOutcome.Active);
        _adapter.Answers["b"] = new StateQueryResult(StateQueryOutcome.Active);
        _adapter.Answers["c"] = new StateQueryResult(StateQueryOutcome.NotActive, "resolved at source 10:00Z");
        // "d" has no scripted answer → Error → unknown.

        var report = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<IDivergenceReporter>().RunAsync(_shadow.Integration.IntegrationId));
        report.Supported.Should().BeTrue();
        report.OpenEpisodes.Should().Be(4);
        report.Agree.Should().Be(2);
        report.Diverged.Should().Be(1);
        report.Unknown.Should().Be(1);
        report.DivergenceShare.Should().BeApproximately(1.0 / 3, 0.001, "diverged over answered; unknown does not count either way");
        report.Threshold.Should().Be(0.25);
        report.WithinThreshold.Should().BeFalse("1/3 > 25 %");
        report.Samples.Should().HaveCount(2);
        report.Samples.Should().ContainSingle(s => s.Outcome == "diverged").Which.EpisodeId.Should().Be(c);
        report.Samples.Should().ContainSingle(s => s.Outcome == "unknown").Which.EpisodeId.Should().Be(d);
        new[] { a, b }.Should().NotContain(report.Samples.Select(s => s.EpisodeId));

        var last = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<IDivergenceReporter>().LastAsync(_shadow.Integration.IntegrationId));
        last.Should().BeEquivalentTo(report, "the report is stored as an audit entry and read back");
        (await TestServices.InDbAsync(_services, db => db.AuditEntries.CountAsync(x => x.Action == DivergenceReporter.Action))).Should().Be(1);

        // The source fixes itself: the next run is within threshold.
        _adapter.Answers["c"] = new StateQueryResult(StateQueryOutcome.Active);
        _adapter.Answers["d"] = new StateQueryResult(StateQueryOutcome.Active);
        var again = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<IDivergenceReporter>().RunAsync(_shadow.Integration.IntegrationId));
        again.Diverged.Should().Be(0);
        again.WithinThreshold.Should().BeTrue();
    }

    [Fact]
    public async Task With_no_open_episodes_the_probe_decides_whether_the_source_is_supported()
    {
        var supported = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<IDivergenceReporter>().RunAsync(_shadow.Integration.IntegrationId));
        supported.Supported.Should().BeTrue();
        supported.OpenEpisodes.Should().Be(0);
        supported.Detail.Should().Be("no open episodes to compare yet");
        supported.WithinThreshold.Should().BeNull();
        _adapter.Supported = false;
        var unsupported = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<IDivergenceReporter>().RunAsync(_shadow.Integration.IntegrationId));
        unsupported.Supported.Should().BeFalse();
        unsupported.Detail.Should().Be("no state api");
    }

    [Fact]
    public async Task Sources_without_a_state_api_report_unsupported_instead_of_a_share()
    {
        await OpenAsync("x");
        _adapter.Supported = false;
        var report = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<IDivergenceReporter>().RunAsync(_shadow.Integration.IntegrationId));
        report.Supported.Should().BeFalse();
        report.Detail.Should().Be("no state api");
        report.DivergenceShare.Should().BeNull();
        report.WithinThreshold.Should().BeNull("no verdict without evidence");
        report.OpenEpisodes.Should().Be(1);
    }
}
