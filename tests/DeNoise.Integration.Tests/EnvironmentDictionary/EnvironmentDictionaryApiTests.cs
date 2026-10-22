using System.Net;
using DeNoise.Contracts;
using DeNoise.Domain.Environments;
using DeNoise.Integration.Tests.Milestone4;
using DeNoise.Integration.Tests.Support;

namespace DeNoise.Integration.Tests.EnvironmentDictionary;

/// <summary>ADR-17: the environment dictionary over HTTP, mapping validation against it, and the queue's unclassified filter.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class EnvironmentDictionaryApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string AdminPassphrase = "Environment-admin-passphrase-17";
    private ApiFixture _api = null!;

    public async Task InitializeAsync() => _api = await ApiFixture.CreateAsync(postgres, "environments");
    public async Task DisposeAsync() => await _api.DisposeAsync();

    private async Task<ApiClient> AdminAsync()
    {
        var admin = await _api.LoginAsync("admin", ApiFixture.AdminPassword);
        using var change = await admin.RawAsync(HttpMethod.Post, "/auth/change-password", new ChangePasswordRequest(ApiFixture.AdminPassword, AdminPassphrase), idempotency: false);
        change.StatusCode.Should().Be(HttpStatusCode.NoContent, await change.Content.ReadAsStringAsync());
        return admin;
    }

    private static string GenericMapping(string target) => $$"""
        mapping:
          fields:
            event_type: { path: "$.eventType" }
            source_alert_id: { path: "$.alertId" }
            rule_id: { path: "$.rule.id" }
            environment: { lookup: { from: "$.sub", map: { "sub-qa": {{target}} }, default: unknown } }
          identity:
            - { name: environment, field: environment }
            - { name: alert, field: source_alert_id }
        """;

    [Fact]
    public async Task Dictionary_is_seeded_in_order_and_readable_by_every_signed_in_user()
    {
        using var viewer = await _api.LoginAsync("viewer");
        var list = (await viewer.GetAsync<List<EnvironmentDto>>("/api/v1/environments"))!;

        list.Select(e => e.Key).Should().Equal(EnvironmentClasses.Seeded, "seed order: production … ephemeral, unknown last");
        list.Single(e => e.Key == "unknown").Should().Match<EnvironmentDto>(e => e.BuiltIn && e.InDefaultView);
        list.Single(e => e.Key == "production").Should().Match<EnvironmentDto>(e => !e.BuiltIn && e.InDefaultView);
        list.Where(e => e.InDefaultView).Select(e => e.Key).Should().BeEquivalentTo(EnvironmentClasses.DefaultView, "spec §15.5 rule 4 fixes the default view");
    }

    [Fact]
    public async Task Changing_the_dictionary_needs_policy_manage()
    {
        using var op = await _api.LoginAsync("operator");
        var (status, _, raw) = await op.PostAsync<EnvironmentDto>("/api/v1/environments", new CreateEnvironmentRequest("qa", "QA"));
        status.Should().Be(HttpStatusCode.Forbidden, raw);
    }

    [Fact]
    public async Task Admin_adds_relabels_and_deletes_a_class_under_if_match_and_every_change_is_audited()
    {
        using var admin = await AdminAsync();

        var (created, qa, raw) = await admin.PostAsync<EnvironmentDto>("/api/v1/environments", new CreateEnvironmentRequest("qa", "QA"));
        created.Should().Be(HttpStatusCode.Created, raw);
        qa!.SortOrder.Should().Be(60, "after the last configured class, before the built-in unknown");
        qa.Version.Should().Be(1);

        (await admin.PostAsync<EnvironmentDto>("/api/v1/environments", new CreateEnvironmentRequest("qa", "Again"))).Status.Should().Be(HttpStatusCode.Conflict);
        (await admin.PostAsync<EnvironmentDto>("/api/v1/environments", new CreateEnvironmentRequest("QA env", "Bad"))).Status.Should().Be(HttpStatusCode.BadRequest);

        using (var noIfMatch = await admin.RawAsync(HttpMethod.Put, "/api/v1/environments/qa", new UpdateEnvironmentRequest("Quality")))
            noIfMatch.StatusCode.Should().Be(HttpStatusCode.PreconditionRequired);
        using (var stale = await admin.RawAsync(HttpMethod.Put, "/api/v1/environments/qa", new UpdateEnvironmentRequest("Quality"), ifMatch: 7))
            stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using (var update = await admin.RawAsync(HttpMethod.Put, "/api/v1/environments/qa", new UpdateEnvironmentRequest("Quality assurance", 25), ifMatch: 1))
        {
            update.StatusCode.Should().Be(HttpStatusCode.OK, await update.Content.ReadAsStringAsync());
            update.Headers.ETag!.Tag.Should().Be("\"2\"");
        }
        var list = (await admin.GetAsync<List<EnvironmentDto>>("/api/v1/environments"))!;
        list.Select(e => e.Key).Should().StartWith(["production", "staging", "qa"], "sort order 25 puts qa between staging and development");
        list.Single(e => e.Key == "qa").Label.Should().Be("Quality assurance");

        using (var builtIn = await admin.RawAsync(HttpMethod.Delete, "/api/v1/environments/unknown", ifMatch: 1))
            builtIn.StatusCode.Should().Be(HttpStatusCode.Conflict, "unknown is what a mapping yields when it cannot derive an environment");
        using (var delete = await admin.RawAsync(HttpMethod.Delete, "/api/v1/environments/qa", ifMatch: 2))
            delete.StatusCode.Should().Be(HttpStatusCode.NoContent, await delete.Content.ReadAsStringAsync());
        (await admin.GetAsync<List<EnvironmentDto>>("/api/v1/environments"))!.Select(e => e.Key).Should().NotContain("qa");

        var audit = await admin.GetAsync<PagedResponse<AuditEntryDto>>("/api/v1/audit?targetType=environment");
        audit!.Items.Select(a => a.Action).Should().BeEquivalentTo(["environment.create", "environment.update", "environment.delete"]);
        audit.Items.Should().OnlyContain(a => a.TargetId == "qa" && a.ActorDisplay == "admin");
    }

    [Fact]
    public async Task Mapping_naming_an_unconfigured_class_is_refused_until_it_exists_and_the_class_cannot_be_deleted_while_an_active_mapping_produces_it()
    {
        using var admin = await AdminAsync();
        var integration = _api.IntegrationA.Integration.IntegrationId;

        // Draft preview and save both point at the offending value.
        using (var preview = await admin.RawAsync(HttpMethod.Post, $"/api/v1/integrations/{integration}/mappings/preview", new MappingPreviewRequest(Yaml: GenericMapping("qa"))))
        {
            preview.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            // System.Text.Json escapes the quotes around the value ('), so match the stable parts.
            (await preview.Content.ReadAsStringAsync()).Should().Contain("$.mapping.fields.environment.lookup.map.sub-qa").And.Contain("not a configured environment class");
        }
        var (refused, _, refusedRaw) = await admin.PostAsync<MappingVersionDto>($"/api/v1/integrations/{integration}/mappings", new CreateMappingRequest(GenericMapping("qa"), "qa-mapping"));
        refused.Should().Be(HttpStatusCode.BadRequest);
        refusedRaw.Should().Contain("$.mapping.fields.environment.lookup.map.sub-qa").And.Contain("not a configured environment class");

        (await admin.PostAsync<EnvironmentDto>("/api/v1/environments", new CreateEnvironmentRequest("qa", "QA"))).Status.Should().Be(HttpStatusCode.Created);
        var (saved, version, savedRaw) = await admin.PostAsync<MappingVersionDto>($"/api/v1/integrations/{integration}/mappings", new CreateMappingRequest(GenericMapping("qa"), "qa-mapping"));
        saved.Should().Be(HttpStatusCode.Created, savedRaw);
        var (activated, _, activatedRaw) = await admin.PostAsync<object>($"/api/v1/integrations/{integration}/mappings/{version!.MappingId}/{version.Version}/activate");
        activated.Should().Be(HttpStatusCode.NoContent, activatedRaw);

        using var delete = await admin.RawAsync(HttpMethod.Delete, "/api/v1/environments/qa", ifMatch: 1);
        delete.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await delete.Content.ReadAsStringAsync()).Should().Contain("qa-mapping");
    }

    [Fact]
    public async Task Unknown_filter_also_matches_values_outside_the_dictionary_so_the_default_view_shows_them()
    {
        var prod = await _api.OpenEpisodeAsync("env-prod", environment: "production");
        var typo = await _api.OpenEpisodeAsync("env-typo", environment: "prodcution");
        var staging = await _api.OpenEpisodeAsync("env-staging", environment: "staging");

        using var op = await _api.LoginAsync("operator");
        var defaultView = await op.GetAsync<PagedResponse<EpisodeListItem>>("/api/v1/episodes?view=needsAttention&environment=production&environment=unknown");
        defaultView!.Items.Select(i => i.Id).Should().BeEquivalentTo([prod, typo], "a value the dictionary does not know is unclassified, never hidden");

        var typoOnly = await op.GetAsync<PagedResponse<EpisodeListItem>>("/api/v1/episodes?view=needsAttention&environment=prodcution");
        typoOnly!.Items.Select(i => i.Id).Should().Equal([typo], "the raw value stays filterable through the URL");

        var stagingView = await op.GetAsync<PagedResponse<EpisodeListItem>>("/api/v1/episodes?view=needsAttention&environment=staging");
        stagingView!.Items.Select(i => i.Id).Should().Equal([staging]);
    }
}
