using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlertHub.Application.Mapping;
using AlertHub.Contracts;
using AlertHub.Integration.Tests.Milestone4;
using AlertHub.Integration.Tests.Support;

namespace AlertHub.Integration.Tests.Milestone8;

/// <summary>06 §4 "Integrations, mappings, replay" over HTTP: seeded versions, draft preview with routing, PUT with If-Match, failures, replay job status.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MappingApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string AdminPassword = "Platform-admin-passphrase-8";
    private ApiFixture _api = null!;

    public async Task InitializeAsync() => _api = await ApiFixture.CreateAsync(postgres, "mappingapi");
    public async Task DisposeAsync() => await _api.DisposeAsync();

    private async Task<ApiClient> AdminAsync()
    {
        var admin = await _api.LoginAsync("admin", ApiFixture.AdminPassword);
        using var change = await admin.RawAsync(HttpMethod.Post, "/auth/change-password", new ChangePasswordRequest(ApiFixture.AdminPassword, AdminPassword), idempotency: false);
        change.StatusCode.Should().Be(HttpStatusCode.NoContent);
        return admin;
    }

    [Fact]
    public async Task Creating_an_azure_integration_seeds_reference_mappings_and_the_editor_can_preview_and_update()
    {
        using var admin = await AdminAsync();
        var (status, created, raw) = await admin.PostAsync<IntegrationCreatedResponse>("/api/v1/integrations", new CreateIntegrationRequest("az-prod", "azure_monitor", "scope-a", _api.TeamA));
        status.Should().Be(HttpStatusCode.Created, raw);
        var id = created!.Integration.Id;
        created.IngestToken.Should().NotBeNullOrEmpty();

        var versions = await admin.GetAsync<List<MappingVersionDto>>($"/api/v1/integrations/{id}/mappings");
        versions!.Should().HaveCount(4).And.OnlyContain(v => v.Active && v.Version == 1);
        versions.Select(v => v.Name).Should().Equal("azure-canary", "azure-resource-health", "azure-service-health", "azure-common-alert-schema");
        versions.Last().Yaml.Should().Contain("lifecycle_profile_hint: explicit_recovery");
        versions.Last().Samples!.Value.GetArrayLength().Should().Be(2);

        // Draft preview against a pasted body: fields, identity and the routing outcome (triage team: no routing policy yet).
        using var body = JsonDocument.Parse(ReferenceMappings.AzureFiredSample);
        var (previewStatus, preview, previewRaw) = await admin.PostAsync<MappingPreviewResponse>($"/api/v1/integrations/{id}/mappings/preview",
            new MappingPreviewRequest(Body: body.RootElement, Yaml: ReferenceMappings.AzureMonitorYaml.Replace("regex_map: {}", "regex_map: { \"/subscriptions/00000000-0000-0000-0000-000000000001.*\": production }", StringComparison.Ordinal)));
        previewStatus.Should().Be(HttpStatusCode.OK, previewRaw);
        var item = preview!.Items.Should().ContainSingle().Subject;
        item.Ok.Should().BeTrue(item.Error);
        item.Fields["event_type"].Should().Be("firing");
        item.Fields["environment"].Should().Be("production");
        item.Identity.Should().Contain(c => c.Name == "environment" && c.Value == "production");
        item.Fingerprint.Should().NotBeNullOrEmpty();
        item.LifecycleProfileHint.Should().Be("explicit_recovery");
        item.Routing!.TeamId.Should().Be(_api.TeamA, "no routing policy: the triage team owns it");

        // A broken draft reports paths instead of a 500.
        using var badPreview = await admin.RawAsync(HttpMethod.Post, $"/api/v1/integrations/{id}/mappings/preview", new MappingPreviewRequest(Body: body.RootElement, Yaml: "mapping:\n  fields:\n    nope: { const: 1 }\n  identity: []\n"));
        badPreview.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await badPreview.Content.ReadAsStringAsync()).Should().Contain("$.mapping.fields.nope");

        // New version of the common schema, stored version preview, activation.
        var common = versions.Last();
        var (v2Status, v2, v2Raw) = await admin.PostAsync<MappingVersionDto>($"/api/v1/integrations/{id}/mappings",
            new CreateMappingRequest(common.Yaml!.Replace("regex_map: {}", "regex_map: { \"/subscriptions/00000000-0000-0000-0000-000000000001.*\": production }", StringComparison.Ordinal), "azure-common-alert-schema", 100, common.MappingId, common.Samples));
        v2Status.Should().Be(HttpStatusCode.Created, v2Raw);
        v2!.Version.Should().Be(2);
        v2.Active.Should().BeFalse();
        var (storedStatus, stored, storedRaw) = await admin.PostAsync<MappingPreviewResponse>($"/api/v1/integrations/{id}/mappings/{common.MappingId}/2/preview", new MappingPreviewRequest(Body: body.RootElement, IncludeRouting: false));
        storedStatus.Should().Be(HttpStatusCode.OK, storedRaw);
        stored!.MappingVersion.Should().Be(2);
        stored.Items[0].Routing.Should().BeNull();
        using var activate = await admin.RawAsync(HttpMethod.Post, $"/api/v1/integrations/{id}/mappings/{common.MappingId}/2/activate");
        activate.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await admin.GetAsync<List<MappingVersionDto>>($"/api/v1/integrations/{id}/mappings"))!.Where(v => v.MappingId == common.MappingId).Select(v => (v.Version, v.Active)).Should().BeEquivalentTo([(2, true), (1, false)]);

        // Integration update creates configuration version 2; a stale If-Match is a conflict; the HMAC secret is never echoed.
        using var put = await admin.RawAsync(HttpMethod.Put, $"/api/v1/integrations/{id}", new UpdateIntegrationRequest(Name: "az-prod-2", HmacSecret: "shh", Hmac: new HmacSettings("sha256", "X-Sig", "hex", true)), ifMatch: 1);
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        var updated = (await put.Content.ReadFromJsonAsync<IntegrationSummary>(ApiClient.Json))!;
        updated.Version.Should().Be(2);
        updated.Name.Should().Be("az-prod-2");
        updated.HmacConfigured.Should().BeTrue();
        updated.Hmac.Should().Be(new HmacSettings("sha256", "X-Sig", "hex", true));
        (await put.Content.ReadAsStringAsync()).Should().NotContain("shh");
        using var stale = await admin.RawAsync(HttpMethod.Put, $"/api/v1/integrations/{id}", new UpdateIntegrationRequest(Name: "again"), ifMatch: 1);
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var noMatch = await admin.RawAsync(HttpMethod.Put, $"/api/v1/integrations/{id}", new UpdateIntegrationRequest(Name: "again"));
        noMatch.StatusCode.Should().Be(HttpStatusCode.PreconditionRequired);

        (await admin.GetAsync<List<ReferenceMappingDto>>("/api/v1/integrations/reference-mappings/atlas"))!.Should().ContainSingle(r => r.Name == "atlas-webhook");
        (await admin.GetAsync<List<MappingFailureDto>>($"/api/v1/integrations/{id}/failures"))!.Should().BeEmpty();
    }

    [Fact]
    public async Task Replay_is_started_as_a_job_with_mode_permissions_and_status_is_readable()
    {
        using var admin = await AdminAsync();
        var id = _api.IntegrationA.Integration.IntegrationId;
        var (status, started, raw) = await admin.PostAsync<ReplayStatus>("/api/v1/replay", new StartReplayRequest("historical", id, From: ApiFixture.T0 - TimeSpan.FromHours(1), To: ApiFixture.T0 + TimeSpan.FromHours(1)));
        status.Should().Be(HttpStatusCode.Accepted, raw);
        started!.Status.Should().Be("pending");
        started.Mode.Should().Be("historical");
        var read = await admin.GetAsync<ReplayStatus>($"/api/v1/replay/{started.JobId}");
        read!.JobId.Should().Be(started.JobId);
        read.Result.Should().BeNull("not run yet");

        using var badMode = await admin.RawAsync(HttpMethod.Post, "/api/v1/replay", new StartReplayRequest("preview", id));
        badMode.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // An operator may not replay at all (RBAC 04 §8); scope-b is invisible to them anyway.
        using var op = await _api.LoginAsync("operator");
        using var forbidden = await op.RawAsync(HttpMethod.Post, "/api/v1/replay", new StartReplayRequest("retry_failed", id));
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var hidden = await op.RawAsync(HttpMethod.Get, $"/api/v1/integrations/{_api.IntegrationB.Integration.IntegrationId}/mappings");
        hidden.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
