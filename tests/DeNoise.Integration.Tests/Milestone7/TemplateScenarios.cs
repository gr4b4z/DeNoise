using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Episodes;
using DeNoise.Application.Ingest;
using DeNoise.Application.Integrations;
using DeNoise.Application.Notifications;
using DeNoise.Application.Notifications.Templates;
using DeNoise.Application.Policies;
using DeNoise.Application.Processing;
using DeNoise.Application.Teams;
using DeNoise.Domain.Notifications;
using DeNoise.Domain.Ops;
using DeNoise.Domain.Policies;
using DeNoise.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace DeNoise.Integration.Tests.Milestone7;

/// <summary>
/// Milestone 7: built-in templates seeded, destinations render through their template (Teams Adaptive Card body, content type),
/// template versioning + preview + activation, destination update with <c>If-Match</c>, test send, and a template that cannot render
/// failing permanently to the fallback (spec §22 <i>Webhook destination fails permanently</i> through the template path).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TemplateScenarios(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    private string _cs = string.Empty;
    private FakeTimeProvider _time = null!;
    private ScriptedChannel _channel = null!;
    private ServiceProvider _services = null!;
    private IntegrationCredentials _integration = null!;
    private Guid _team;
    private Guid _teams;      // destination with the teams-adaptive-card template
    private Guid _fallback;   // its fallback (generic body)

    public async Task InitializeAsync()
    {
        _cs = await postgres.CreateDatabaseAsync("templates");
        _time = new FakeTimeProvider(T0);
        _channel = new ScriptedChannel();
        _services = TestServices.Build(_cs, _time, s =>
        {
            s.RemoveAll<INotificationChannel>();
            s.AddSingleton<INotificationChannel>(_channel);
        }, new Dictionary<string, string?> { ["Notifications:AllowInsecureDestinations"] = "true", ["Notifications:PublicBaseUrl"] = "https://hub.test" });
        var actor = Actor.System("setup");
        await TestServices.InScopeAsync(_services, async sp =>
        {
            (await sp.GetRequiredService<TemplateService>().EnsureBuiltInsAsync()).Should().Be(4);
            var team = await sp.GetRequiredService<TeamService>().CreateAsync(new CreateTeam("mpt", ["scope-a"], IsTriage: true), actor);
            _team = team.TeamId;
            var destinations = sp.GetRequiredService<DestinationService>();
            var (primary, fallback) = await destinations.CreatePairAsync(
                new CreateDestination("teams-workflow", ChannelTypes.Webhook, _team, null, Url: "http://receiver.test/teams"),
                new CreateDestination("generic-fallback", ChannelTypes.Webhook, _team, null, Url: "http://receiver.test/fallback"), actor);
            _teams = primary.Destination.DestinationId;
            _fallback = fallback.Destination.DestinationId;
            await destinations.UpdateAsync(_teams, primary.Destination.Version, new UpdateDestination(BodyTemplateId: BuiltInTemplates.TeamsAdaptiveCard), actor);
            _integration = await sp.GetRequiredService<IntegrationService>().CreateAsync(new CreateIntegration("generic", "generic_webhook", "scope-a", _team), actor);
        });
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    private async Task<Guid> OpenAsync(string alertId)
    {
        var body = $$"""{"eventType":"firing","alertId":"{{alertId}}","eventId":"{{Guid.NewGuid()}}","occurredAt":"{{_time.GetUtcNow():O}}","severity":"critical","environment":"production","service":"orders","resource":{"id":"res-{{alertId}}","name":"Orders \"API\""},"rule":{"id":"5xx","name":"5xx rate"},"summary":"5xx > 2% on orders <prod>"}""";
        var accepted = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<IngestService>()
            .AcceptAsync(_integration.Integration, new IngestRequest(Encoding.UTF8.GetBytes(body), "application/json", new Dictionary<string, string>(), null)));
        var result = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<EventProcessor>().ProcessAsync(new NormaliseJobPayload(accepted.EventId, accepted.ReceivedAt, _integration.Integration.IntegrationId)));
        return result.EpisodeId!.Value;
    }

    private Task<IReadOnlyList<(OutboxMessage Message, DispatchOutcome Outcome)>> DispatchAsync()
        => TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync("dispatcher-1", 50, CancellationToken.None));

    [Fact]
    public async Task Destination_with_the_teams_template_receives_an_adaptive_card_and_the_fallback_the_generic_model()
    {
        var episode = await OpenAsync("t1");
        await DispatchAsync();

        var toTeams = _channel.Sent.Single(s => s.Destination.Destination.DestinationId == _teams && s.Message.Type == NotificationTypes.EpisodeOpened);
        toTeams.Destination.ContentType.Should().Be("application/json");
        using var card = JsonDocument.Parse(toTeams.Body);
        var content = card.RootElement.GetProperty("attachments")[0].GetProperty("content");
        content.GetProperty("type").GetString().Should().Be("AdaptiveCard");
        content.GetProperty("body")[0].GetProperty("text").GetString().Should().Be("CRITICAL · 5xx > 2% on orders <prod>", "payload text is a value, never markup");
        content.GetProperty("actions")[0].GetProperty("url").GetString().Should().Be($"https://hub.test/episodes/{episode}");
        var facts = content.GetProperty("body")[2].GetProperty("facts");
        facts[0].GetProperty("value").GetString().Should().Be("Orders \"API\"", "quotes in the resource name are escaped, not broken");

        var toFallback = _channel.Sent.Single(s => s.Destination.Destination.DestinationId == _fallback && s.Message.Type == NotificationTypes.EpisodeOpened);
        using var generic = JsonDocument.Parse(toFallback.Body);
        generic.RootElement.GetProperty("event").GetString().Should().Be(NotificationTypes.EpisodeOpened);
        generic.RootElement.GetProperty("episode").GetProperty("id").GetString().Should().Be(episode.ToString());
    }

    [Fact]
    public async Task Template_versions_render_preview_and_activation_switches_the_body_sent()
    {
        var actor = Actor.System("admin");
        var (created, preview) = await TestServices.InScopeAsync(_services, async sp =>
        {
            var service = sp.GetRequiredService<TemplateService>();
            var v1 = await service.CreateVersionAsync(new CreateTemplateVersion("ticket", TemplateFormats.Json, """{ "title": "[{{ episode.severity }}] {{ episode.summary }}", "url": "{{ episode.url }}", "event": "{{ event }}" }"""), actor);
            v1.IsActive.Should().BeFalse("new versions are inactive until activated");
            v1.SampleOutput.Should().Contain("\"title\": \"[critical]");
            var preview = await service.RenderPreviewAsync(v1.TemplateId, 1, null, null);
            await service.ActivateAsync(v1.TemplateId, 1, actor);
            return (v1, preview);
        });
        JsonDocument.Parse(preview.Body).RootElement.GetProperty("event").GetString().Should().Be(NotificationTypes.EpisodeOpened);

        await TestServices.InScopeAsync(_services, async sp =>
        {
            var destinations = sp.GetRequiredService<DestinationService>();
            var current = (await sp.GetRequiredService<IDestinationRepository>().GetAsync(_teams))!;
            await destinations.UpdateAsync(_teams, current.Version, new UpdateDestination(BodyTemplateId: created.TemplateId), actor);
        });
        var episode = await OpenAsync("t2");
        await DispatchAsync();
        var sent = _channel.Sent.Single(s => s.Destination.Destination.DestinationId == _teams && s.Message.EpisodeId == episode && s.Message.Type == NotificationTypes.EpisodeOpened);
        JsonDocument.Parse(sent.Body).RootElement.GetProperty("title").GetString().Should().Be("[critical] 5xx > 2% on orders <prod>");

        // A broken version is refused at creation; a stale If-Match on the destination is a conflict.
        await TestServices.InScopeAsync(_services, async sp =>
        {
            var service = sp.GetRequiredService<TemplateService>();
            var broken = () => service.CreateVersionAsync(new CreateTemplateVersion("ticket", TemplateFormats.Json, """{ "title": {{ episode.summary }} }""", TemplateId: created.TemplateId), actor);
            await broken.Should().ThrowAsync<Application.Mapping.MappingValidationException>();
            var readOnly = () => service.CreateVersionAsync(new CreateTemplateVersion("generic-json", TemplateFormats.Json, "{}", TemplateId: BuiltInTemplates.GenericJson), actor);
            await readOnly.Should().ThrowAsync<InvalidOperationException>();
            var stale = () => sp.GetRequiredService<DestinationService>().UpdateAsync(_teams, 1, new UpdateDestination(Name: "renamed"), actor);
            await stale.Should().ThrowAsync<VersionConflictException>();
        });
    }

    [Fact]
    public async Task Test_send_goes_through_the_channel_with_the_rendered_body_and_reports_the_outcome()
    {
        _channel.Script.Enqueue(_ => ChannelResult.Success(202, "accepted by workflow"));
        var result = await TestServices.InScopeAsync(_services, sp => sp.GetRequiredService<DestinationService>().TestSendAsync(_teams, Actor.System("admin")));
        result.Outcome.Should().Be(DeliveryOutcomes.Success);
        result.HttpStatus.Should().Be(202);
        result.ResponseExcerpt.Should().Be("accepted by workflow");
        JsonDocument.Parse(result.RenderedBody).RootElement.GetProperty("attachments")[0].GetProperty("content").GetProperty("type").GetString().Should().Be("AdaptiveCard");
        var sent = _channel.Sent.Single();
        sent.Message.Type.Should().Be(NotificationTypes.DestinationTest);
        await using var db = PostgresFixture.CreateContext(_cs);
        var row = await db.Outbox.AsNoTracking().SingleAsync(o => o.Type == NotificationTypes.DestinationTest);
        row.Status.Should().Be(OutboxStatus.Sent, "a test send is recorded terminal: visible in deliveries, never retried");
        (await db.DeliveryAttempts.AsNoTracking().SingleAsync(a => a.OutboxId == row.OutboxId)).HttpStatus.Should().Be(202);
        (await db.Destinations.AsNoTracking().SingleAsync(d => d.DestinationId == _teams)).LastSuccessAt.Should().Be(_time.GetUtcNow());
    }

    [Fact]
    public async Task Scenario_WebhookDestinationFailsPermanently_via_a_template_that_no_longer_renders_uses_the_fallback()
    {
        // Point the destination at a template whose only active version is then deactivated by activating a broken-at-render one: simulate by a template with no active version.
        var actor = Actor.System("admin");
        var orphan = await TestServices.InScopeAsync(_services, async sp =>
        {
            var service = sp.GetRequiredService<TemplateService>();
            var v1 = await service.CreateVersionAsync(new CreateTemplateVersion("orphan", TemplateFormats.Json, """{ "ok": "{{ event }}" }"""), actor);
            await service.ActivateAsync(v1.TemplateId, 1, actor);
            var destinations = sp.GetRequiredService<DestinationService>();
            var current = (await sp.GetRequiredService<IDestinationRepository>().GetAsync(_teams))!;
            await destinations.UpdateAsync(_teams, current.Version, new UpdateDestination(BodyTemplateId: v1.TemplateId), actor);
            return v1.TemplateId;
        });
        // The active version disappears (operator error, restore from backup, …): the dispatcher must not guess a body.
        await TestServices.InDbAsync(_services, db => db.WebhookTemplates.Where(t => t.TemplateId == orphan).ExecuteUpdateAsync(u => u.SetProperty(t => t.DeactivatedAt, _time.GetUtcNow())));

        var episode = await OpenAsync("t4");
        await DispatchAsync();
        await using var db = PostgresFixture.CreateContext(_cs);
        var rows = await db.Outbox.AsNoTracking().Where(o => o.EpisodeId == episode).ToListAsync();
        var toTeams = rows.Single(o => o.DestinationId == _teams && o.Type == NotificationTypes.EpisodeOpened);
        toTeams.Status.Should().Be(OutboxStatus.Failed);
        toTeams.LastError.Should().Contain("template render failed");
        rows.Should().Contain(o => o.DestinationId == _fallback && o.Type == NotificationTypes.HubDeliveryFailure, "permanent failure raises hub.delivery_failure to the fallback");
        var attempt = await db.DeliveryAttempts.AsNoTracking().SingleAsync(a => a.OutboxId == toTeams.OutboxId);
        attempt.Outcome.Should().Be(DeliveryOutcomes.Permanent);
        _channel.Sent.Should().NotContain(s => s.Destination.Destination.DestinationId == _teams && s.Message.EpisodeId == episode, "nothing was sent with a guessed body");
    }

    private sealed class ScriptedChannel : INotificationChannel
    {
        public string ChannelType => ChannelTypes.Webhook;
        public ConcurrentQueue<Func<OutboxMessage, ChannelResult>> Script { get; } = new();
        public List<(OutboxMessage Message, string Body, ResolvedDestination Destination)> Sent { get; } = [];

        public Task<ChannelResult> SendAsync(ResolvedDestination destination, OutboxMessage message, string body, CancellationToken ct)
        {
            lock (Sent) Sent.Add((message, body, destination));
            return Task.FromResult(Script.TryDequeue(out var step) ? step(message) : ChannelResult.Success(200));
        }
    }
}
