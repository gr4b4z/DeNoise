using System.Net;
using DeNoise.Application.Policies;
using DeNoise.Contracts;
using DeNoise.Integration.Tests.Milestone4;
using DeNoise.Integration.Tests.Support;

namespace DeNoise.Integration.Tests.Milestone10;

/// <summary>06 §4 policies over HTTP (versions, impact, rollback), 06 §8 config-as-code (<c>/config/export</c>, <c>/config/import</c>) and the audit log (<c>/audit</c>).</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ConfigAndAuditApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string AdminPassphrase = "Platform-admin-passphrase-9";
    private ApiFixture _api = null!;

    public async Task InitializeAsync() => _api = await ApiFixture.CreateAsync(postgres, "configaudit");
    public async Task DisposeAsync() => await _api.DisposeAsync();

    private async Task<ApiClient> AdminAsync()
    {
        var admin = await _api.LoginAsync("admin", ApiFixture.AdminPassword);
        using var change = await admin.RawAsync(HttpMethod.Post, "/auth/change-password", new ChangePasswordRequest(ApiFixture.AdminPassword, AdminPassphrase), idempotency: false);
        change.StatusCode.Should().Be(HttpStatusCode.NoContent, await change.Content.ReadAsStringAsync());
        return admin;
    }

    private static string Lifecycle(string silence) => $"lifecycle: {{ profile: repeating_while_active }}\nauto_resolve: {{ after_silence: {silence} }}";

    private string Routing(Guid escalation) => $$"""
        routing:
          rules:
            - name: orders-prod
              priority: 10
              match: { all: [ { eq: [environment, production] }, { eq: [service, orders] } ] }
              team: {{_api.TeamA}}
              escalation_policy: {{escalation}}
        """;

    private const string Escalation = """
        escalation:
          ack_deadline: 15m
          steps:
            - { after: 0m, targets: [team_destinations] }
          repeat_last_step_every: 30m
          max_repeats: 1
        """;

    private const string Grouping = """
        grouping:
          rules:
            - name: by-service
              match: { exists: "service" }
              key: [service, environment]
              window: 10m
              notify: first_and_new_critical
        """;

    [Fact]
    public async Task Policy_versions_activate_impact_and_rollback_over_http()
    {
        using var admin = await AdminAsync();
        var (status, v1, raw) = await admin.PostAsync<PolicyVersionDto>("/api/v1/policies/lifecycle", new CreatePolicyRequest(Lifecycle("45m"), null, "repeating"));
        status.Should().Be(HttpStatusCode.Created, raw);
        v1!.Version.Should().Be(1);
        v1.Active.Should().BeFalse("versions start inactive; activation is explicit");
        using var activate = await admin.RawAsync(HttpMethod.Post, $"/api/v1/policies/lifecycle/{v1.Id}/1/activate");
        activate.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var episode = await _api.OpenEpisodeAsync("cfg-1");
        var (s2, v2, raw2) = await admin.PostAsync<PolicyVersionDto>("/api/v1/policies/lifecycle", new CreatePolicyRequest(Lifecycle("2h"), v1.Id, "repeating"));
        s2.Should().Be(HttpStatusCode.Created, raw2);
        v2!.Version.Should().Be(2);
        var (impactStatus, impact, impactRaw) = await admin.PostAsync<PolicyImpactDto>($"/api/v1/policies/lifecycle/{v1.Id}/2/impact");
        impactStatus.Should().Be(HttpStatusCode.OK, impactRaw);
        impact!.Explanation.Should().NotBeNullOrEmpty();
        impact.Kind.Should().Be("lifecycle");

        using var activate2 = await admin.RawAsync(HttpMethod.Post, $"/api/v1/policies/lifecycle/{v1.Id}/2/activate");
        activate2.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var versions = await admin.GetAsync<List<PolicyVersionDto>>($"/api/v1/policies/lifecycle/{v1.Id}/versions");
        versions!.Single(v => v.Active).Version.Should().Be(2);
        versions.Should().AllSatisfy(v => v.Yaml.Should().NotBeNullOrEmpty());

        using var rollback = await admin.RawAsync(HttpMethod.Post, $"/api/v1/policies/lifecycle/{v1.Id}/rollback", new RollbackRequest(1));
        rollback.StatusCode.Should().Be(HttpStatusCode.NoContent, await rollback.Content.ReadAsStringAsync());
        versions = await admin.GetAsync<List<PolicyVersionDto>>($"/api/v1/policies/lifecycle/{v1.Id}/versions");
        versions!.Single(v => v.Active).Version.Should().Be(1, "rollback = activate an earlier version");
        (await admin.GetAsync<List<PolicyVersionDto>>("/api/v1/policies/lifecycle"))!.Select(v => (v.Id, v.Version)).Should().Equal((v1.Id, 1));

        using var badKind = await admin.RawAsync(HttpMethod.Get, "/api/v1/policies/weather");
        badKind.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var invalid = await admin.RawAsync(HttpMethod.Post, "/api/v1/policies/lifecycle", new CreatePolicyRequest("lifecycle: { profile: nope }", null, null));
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var op = await _api.LoginAsync("operator");
        using var forbidden = await op.RawAsync(HttpMethod.Post, "/api/v1/policies/lifecycle", new CreatePolicyRequest(Lifecycle("1h"), null, null));
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden, "operators cannot manage policies");
        episode.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Export_import_round_trip_is_unchanged_and_edits_create_new_active_versions()
    {
        using var admin = await AdminAsync();
        var (es, escalation, eraw) = await admin.PostAsync<PolicyVersionDto>("/api/v1/policies/escalation", new CreatePolicyRequest(Escalation, null, "default"));
        es.Should().Be(HttpStatusCode.Created, eraw);
        (await admin.RawAsync(HttpMethod.Post, $"/api/v1/policies/escalation/{escalation!.Id}/1/activate")).Dispose();
        var (rs, routing, rraw) = await admin.PostAsync<PolicyVersionDto>("/api/v1/policies/routing", new CreatePolicyRequest(Routing(escalation.Id), null, null));
        rs.Should().Be(HttpStatusCode.Created, rraw);
        routing!.Id.Should().Be(WellKnownPolicies.Routing);
        (await admin.RawAsync(HttpMethod.Post, $"/api/v1/policies/routing/{routing.Id}/1/activate")).Dispose();
        var (gs, grouping, graw) = await admin.PostAsync<PolicyVersionDto>("/api/v1/policies/grouping", new CreatePolicyRequest(Grouping, null, null));
        gs.Should().Be(HttpStatusCode.Created, graw);
        (await admin.RawAsync(HttpMethod.Post, $"/api/v1/policies/grouping/{grouping!.Id}/1/activate")).Dispose();
        var (ls, lifecycle, lraw) = await admin.PostAsync<PolicyVersionDto>("/api/v1/policies/lifecycle", new CreatePolicyRequest(Lifecycle("45m"), null, "repeating"));
        ls.Should().Be(HttpStatusCode.Created, lraw);
        (await admin.RawAsync(HttpMethod.Post, $"/api/v1/policies/lifecycle/{lifecycle!.Id}/1/activate")).Dispose();

        using var export = await admin.RawAsync(HttpMethod.Get, "/api/v1/config/export", csrf: false, idempotency: false);
        export.StatusCode.Should().Be(HttpStatusCode.OK);
        export.Content.Headers.ContentType!.MediaType.Should().Be("application/yaml");
        var yaml = await export.Content.ReadAsStringAsync();
        yaml.Should().Contain("denoise_config: 1").And.Contain("routing:").And.Contain("escalation:").And.Contain("lifecycle:").And.Contain("grouping:")
            .And.Contain(escalation.Id.ToString()).And.Contain("after_silence: 45m");

        var (ds, dry, draw) = await admin.PostAsync<ConfigImportResponse>("/api/v1/config/import", new ConfigImportRequest(yaml, DryRun: true));
        ds.Should().Be(HttpStatusCode.OK, draw);
        dry!.DryRun.Should().BeTrue();
        dry.Errors.Should().BeEmpty();
        dry.HasChanges.Should().BeFalse("re-importing an export is a no-op");
        dry.Entries.Should().HaveCount(4).And.AllSatisfy(e => e.Action.Should().Be("unchanged"));

        var edited = yaml.Replace("after_silence: 45m", "after_silence: 2h", StringComparison.Ordinal);
        var (ps, preview, praw) = await admin.PostAsync<ConfigImportResponse>("/api/v1/config/import", new ConfigImportRequest(edited, DryRun: true));
        ps.Should().Be(HttpStatusCode.OK, praw);
        preview!.HasChanges.Should().BeTrue();
        var change = preview.Entries.Single(e => e.Action == "new_version");
        change.Kind.Should().Be("lifecycle");
        change.PolicyId.Should().Be(lifecycle.Id);
        change.Version.Should().BeNull("dry run creates nothing");
        change.Changes.Should().Equal("~ auto_resolve");
        (await admin.GetAsync<List<PolicyVersionDto>>($"/api/v1/policies/lifecycle/{lifecycle.Id}/versions"))!.Should().HaveCount(1, "dry run must not write");

        var (as_, applied, araw) = await admin.PostAsync<ConfigImportResponse>("/api/v1/config/import", new ConfigImportRequest(edited, DryRun: false));
        as_.Should().Be(HttpStatusCode.OK, araw);
        applied!.Errors.Should().BeEmpty();
        applied.Entries.Single(e => e.Action == "new_version").Version.Should().Be(2);
        var versions = await admin.GetAsync<List<PolicyVersionDto>>($"/api/v1/policies/lifecycle/{lifecycle.Id}/versions");
        versions!.Single(v => v.Active).Version.Should().Be(2);
        using var again = await admin.RawAsync(HttpMethod.Get, "/api/v1/config/export", csrf: false, idempotency: false);
        (await again.Content.ReadAsStringAsync()).Should().Contain("after_silence: 2h").And.NotContain("after_silence: 45m");

        var (bs, bad, braw) = await admin.PostAsync<ConfigImportResponse>("/api/v1/config/import", new ConfigImportRequest("denoise_config: 1\npolicies:\n  weather: []\n  lifecycle:\n    - document: { lifecycle: { profile: repeating_while_active } }\n", DryRun: true));
        bs.Should().Be(HttpStatusCode.OK, braw);
        bad!.Errors.Should().HaveCount(2).And.Contain(e => e.Contains("unknown policy kind")).And.Contain(e => e.Contains("need an 'id'"));
        using var garbage = await admin.RawAsync(HttpMethod.Post, "/api/v1/config/import", new ConfigImportRequest("just a string", DryRun: true));
        var garbageBody = await garbage.Content.ReadAsStringAsync();
        garbage.StatusCode.Should().Be(HttpStatusCode.OK, garbageBody);
        garbageBody.Should().Contain("'policies' object");

        using var op = await _api.LoginAsync("operator");
        using var opExport = await op.RawAsync(HttpMethod.Get, "/api/v1/config/export", csrf: false, idempotency: false);
        opExport.StatusCode.Should().Be(HttpStatusCode.OK, "export is read-only");
        using var opImport = await op.RawAsync(HttpMethod.Post, "/api/v1/config/import", new ConfigImportRequest(yaml, DryRun: true));
        opImport.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Audit_log_lists_newest_first_with_filters_and_keyset_paging()
    {
        using var admin = await AdminAsync();
        var (s, v1, raw) = await admin.PostAsync<PolicyVersionDto>("/api/v1/policies/lifecycle", new CreatePolicyRequest(Lifecycle("45m"), null, "repeating"));
        s.Should().Be(HttpStatusCode.Created, raw);
        for (var i = 1; i <= 3; i++)
        {
            _api.Time.Advance(TimeSpan.FromMinutes(1));
            (await admin.PostAsync<PolicyVersionDto>("/api/v1/policies/lifecycle", new CreatePolicyRequest(Lifecycle($"{i}h"), v1!.Id, "repeating"))).Status.Should().Be(HttpStatusCode.Created);
        }
        _api.Time.Advance(TimeSpan.FromMinutes(1));
        (await admin.RawAsync(HttpMethod.Post, $"/api/v1/policies/lifecycle/{v1!.Id}/4/activate")).Dispose();

        using var viewer = await _api.LoginAsync("viewer");
        var all = await viewer.GetAsync<PagedResponse<AuditEntryDto>>($"/api/v1/audit?target={v1.Id}");
        all!.Items.Select(a => a.Action).Should().Equal("policy.lifecycle.activate", "policy.lifecycle.create_version", "policy.lifecycle.create_version", "policy.lifecycle.create_version", "policy.lifecycle.create_version");
        all.Items.Should().BeInDescendingOrder(a => a.At);
        all.Items[0].After!.Value.GetProperty("version").GetInt32().Should().Be(4);
        all.Items[0].ActorDisplay.Should().Be("admin");
        all.Items[0].TargetType.Should().Be("policy");
        all.NextCursor.Should().BeNull();

        var page1 = await viewer.GetAsync<PagedResponse<AuditEntryDto>>($"/api/v1/audit?target={v1.Id}&limit=2");
        page1!.Items.Should().HaveCount(2);
        page1.NextCursor.Should().NotBeNull();
        var page2 = await viewer.GetAsync<PagedResponse<AuditEntryDto>>($"/api/v1/audit?target={v1.Id}&limit=2&cursor={Uri.EscapeDataString(page1.NextCursor!)}");
        var page3 = await viewer.GetAsync<PagedResponse<AuditEntryDto>>($"/api/v1/audit?target={v1.Id}&limit=2&cursor={Uri.EscapeDataString(page2!.NextCursor!)}");
        page3!.NextCursor.Should().BeNull();
        page1.Items.Concat(page2.Items).Concat(page3.Items).Select(a => a.Id).Should().Equal(all.Items.Select(a => a.Id), "pages stitch together without gaps or duplicates");

        (await viewer.GetAsync<PagedResponse<AuditEntryDto>>("/api/v1/audit?action=policy.lifecycle.activate"))!.Items.Should().ContainSingle();
        (await viewer.GetAsync<PagedResponse<AuditEntryDto>>("/api/v1/audit?action=policy"))!.Items.Should().HaveCount(5, "action filters match the dotted prefix");
        (await viewer.GetAsync<PagedResponse<AuditEntryDto>>("/api/v1/audit?actor=admin&targetType=policy"))!.Items.Should().HaveCount(5);
        (await viewer.GetAsync<PagedResponse<AuditEntryDto>>("/api/v1/audit?actor=nobody"))!.Items.Should().BeEmpty();
        var from = ApiFixture.T0 + TimeSpan.FromMinutes(3.5);
        var late = await viewer.GetAsync<PagedResponse<AuditEntryDto>>($"/api/v1/audit?targetType=policy&from={Uri.EscapeDataString(from.ToString("O"))}");
        late!.Items.Select(a => a.Action).Should().Equal("policy.lifecycle.activate");
        var early = await viewer.GetAsync<PagedResponse<AuditEntryDto>>($"/api/v1/audit?targetType=policy&to={Uri.EscapeDataString(from.ToString("O"))}");
        early!.Items.Should().HaveCount(4);
        (await viewer.GetAsync<PagedResponse<AuditEntryDto>>("/api/v1/audit?action=user"))!.Items.Should().NotBeEmpty("the fixture's user creation is audited too");
    }
}
