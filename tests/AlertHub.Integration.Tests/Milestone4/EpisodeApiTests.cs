using System.Net;
using System.Text.Json;
using AlertHub.Contracts;
using AlertHub.Domain.Episodes;
using AlertHub.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace AlertHub.Integration.Tests.Milestone4;

/// <summary>Spec §22: <i>Two users take ownership</i>, <i>User lacks resource access</i>, <i>Manual close while source is firing</i> (API half); plus the 06 §1 mutation contract.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class EpisodeApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private ApiFixture _api = null!;

    public async Task InitializeAsync() => _api = await ApiFixture.CreateAsync(postgres, "episodes");
    public async Task DisposeAsync() => await _api.DisposeAsync();

    [Fact]
    public async Task Queue_lists_only_visible_scopes_with_cursor_paging_counts_and_detail()
    {
        var a1 = await _api.OpenEpisodeAsync("q-1", severity: "critical");
        var a2 = await _api.OpenEpisodeAsync("q-2", severity: "low");
        var a3 = await _api.OpenEpisodeAsync("q-3", severity: "high");
        var b1 = await _api.OpenEpisodeAsync("q-b", _api.IntegrationB);

        using var op = await _api.LoginAsync("operator");
        var page1 = await op.GetAsync<PagedResponse<EpisodeListItem>>("/api/v1/episodes?view=needsAttention&limit=2&includeTotal=true");
        page1!.Items.Select(i => i.Id).Should().Equal([a1, a3], "severity order: critical, high, low");
        page1.Total.Should().Be(3, "scope-b is invisible");
        page1.NextCursor.Should().NotBeNull();
        var page2 = await op.GetAsync<PagedResponse<EpisodeListItem>>($"/api/v1/episodes?view=needsAttention&limit=2&cursor={Uri.EscapeDataString(page1.NextCursor!)}");
        page2!.Items.Select(i => i.Id).Should().Equal(a2);
        page2.NextCursor.Should().BeNull();
        page1.Items.Select(i => i.Id).Should().NotContain(b1);

        var filtered = await op.GetAsync<PagedResponse<EpisodeListItem>>("/api/v1/episodes?severity=critical&severity=high");
        filtered!.Items.Should().HaveCount(2);
        var searched = await op.GetAsync<PagedResponse<EpisodeListItem>>("/api/v1/episodes?q=q-2");
        searched!.Items.Should().ContainSingle(i => i.Id == a2);

        var counts = await op.GetAsync<Dictionary<string, int>>("/api/v1/episodes/counts");
        counts!["needsAttention"].Should().Be(3);
        counts["myTeams"].Should().Be(3, "operator is a member of team-a, which owns scope-a episodes");
        counts["unassigned"].Should().Be(3);

        var detail = await op.GetAsync<EpisodeDetail>($"/api/v1/episodes/{a1}");
        detail!.Item.Severity.Should().Be("critical");
        detail.Item.OwningTeam!.Id.Should().Be(_api.TeamA);
        detail.IdentityComponents.Should().Contain(c => c.Name == "rule" && c.Value == "5xx");
        detail.Explanation.Should().Contain("critical alert").And.Contain("Seen 1 time");
        detail.RawPayloadAvailable.Should().BeTrue();
        var timeline = await op.GetAsync<PagedResponse<TimelineEntry>>($"/api/v1/episodes/{a1}/timeline");
        timeline!.Items.Select(t => t.Kind).Should().Contain(["source_event", "assign"]);
    }

    [Fact]
    public async Task Scenario_UserLacksResourceAccess_episode_in_another_scope_is_404_via_api()
    {
        var b = await _api.OpenEpisodeAsync("scope-b-1", _api.IntegrationB);
        using var op = await _api.LoginAsync("operator");
        using var detail = await op.RawAsync(HttpMethod.Get, $"/api/v1/episodes/{b}");
        detail.StatusCode.Should().Be(HttpStatusCode.NotFound, "existence must not leak");
        using var ack = await op.RawAsync(HttpMethod.Post, $"/api/v1/episodes/{b}/ack", new AckRequest(), ifMatch: 1);
        ack.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var timeline = await op.RawAsync(HttpMethod.Get, $"/api/v1/episodes/{b}/timeline");
        timeline.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var other = await _api.LoginAsync("other");
        (await other.GetAsync<EpisodeDetail>($"/api/v1/episodes/{b}"))!.Item.Id.Should().Be(b);
        using var admin = await _api.LoginAsync("admin", ApiFixture.AdminPassword);
        using var change = await admin.RawAsync(HttpMethod.Post, "/auth/change-password", new ChangePasswordRequest(ApiFixture.AdminPassword, "Platform-admin-passphrase-9"), idempotency: false);
        (await admin.GetAsync<EpisodeDetail>($"/api/v1/episodes/{b}"))!.Item.Id.Should().Be(b, "platform_admin sees every scope");
    }

    [Fact]
    public async Task Mutations_require_if_match_and_idempotency_key_and_replay_the_original_response()
    {
        var id = await _api.OpenEpisodeAsync("mut-1");
        using var op = await _api.LoginAsync("operator");

        using var noIfMatch = await op.RawAsync(HttpMethod.Post, $"/api/v1/episodes/{id}/ack", new AckRequest());
        noIfMatch.StatusCode.Should().Be(HttpStatusCode.PreconditionRequired);
        using var noKey = await op.RawAsync(HttpMethod.Post, $"/api/v1/episodes/{id}/ack", new AckRequest(), ifMatch: 1, idempotency: false);
        noKey.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await noKey.Content.ReadAsStringAsync()).Should().Contain("idempotency-key-required");

        var key = Guid.NewGuid();
        var (s1, first, raw1) = await op.PostAsync<EpisodeDetail>($"/api/v1/episodes/{id}/note", new NoteRequest("looking into it"), ifMatch: 1, idempotencyKey: key);
        s1.Should().Be(HttpStatusCode.OK, raw1);
        first!.Item.Version.Should().Be(2);
        var (s2, replay, raw2) = await op.PostAsync<EpisodeDetail>($"/api/v1/episodes/{id}/note", new NoteRequest("looking into it"), ifMatch: 1, idempotencyKey: key);
        s2.Should().Be(HttpStatusCode.OK);
        replay!.Item.Version.Should().Be(2, "replayed, not re-applied");
        raw2.Should().Be(raw1);
        using var reused = await op.RawAsync(HttpMethod.Post, $"/api/v1/episodes/{id}/note", new NoteRequest("different text"), ifMatch: 2, idempotencyKey: key);
        reused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        await using var db = PostgresFixture.CreateContext(_api.ConnectionString);
        (await db.EpisodeEvents.CountAsync(e => e.EpisodeId == id && e.Kind == EpisodeEventKind.Note)).Should().Be(1);
    }

    [Fact]
    public async Task Scenario_TwoUsersTakeOwnership_one_wins_the_other_gets_409_with_current_state()
    {
        var id = await _api.OpenEpisodeAsync("race-1");
        using var olga = await _api.LoginAsync("operator");
        using var admin = await _api.LoginAsync("admin", ApiFixture.AdminPassword);
        using var _ = await admin.RawAsync(HttpMethod.Post, "/auth/change-password", new ChangePasswordRequest(ApiFixture.AdminPassword, "Platform-admin-passphrase-8"), idempotency: false);

        var t1 = olga.RawAsync(HttpMethod.Post, $"/api/v1/episodes/{id}/ack", new AckRequest(), ifMatch: 1);
        var t2 = admin.RawAsync(HttpMethod.Post, $"/api/v1/episodes/{id}/ack", new AckRequest(), ifMatch: 1);
        var responses = await Task.WhenAll(t1, t2);

        responses.Select(r => r.StatusCode).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        var conflict = responses.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        using var doc = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("type").GetString().Should().Be("urn:alerthub:error:version-conflict");
        var current = doc.RootElement.GetProperty("current").GetProperty("item");
        current.GetProperty("handlingState").GetString().Should().Be("acknowledged");
        current.GetProperty("version").GetInt32().Should().Be(2);
        current.GetProperty("assignee").GetProperty("username").GetString().Should().BeOneOf("operator", "admin");

        var detail = await olga.GetAsync<EpisodeDetail>($"/api/v1/episodes/{id}");
        detail!.Item.HandlingState.Should().Be("acknowledged");
        detail.AcknowledgedBy.Should().NotBeNull();
        foreach (var r in responses) r.Dispose();
    }

    [Fact]
    public async Task Takeover_requires_force_and_assignment_does_not_reset_the_ack_deadline()
    {
        var id = await _api.OpenEpisodeAsync("take-1");
        using var olga = await _api.LoginAsync("operator");
        using var admin = await _api.LoginAsync("admin", ApiFixture.AdminPassword);
        using var _ = await admin.RawAsync(HttpMethod.Post, "/auth/change-password", new ChangePasswordRequest(ApiFixture.AdminPassword, "Platform-admin-passphrase-7"), idempotency: false);

        var (s1, v2, _) = await olga.PostAsync<EpisodeDetail>($"/api/v1/episodes/{id}/ack", new AckRequest(), ifMatch: 1);
        s1.Should().Be(HttpStatusCode.OK);
        using var noForce = await admin.RawAsync(HttpMethod.Post, $"/api/v1/episodes/{id}/ack", new AckRequest(), ifMatch: v2!.Item.Version);
        noForce.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await noForce.Content.ReadAsStringAsync()).Should().Contain("invalid-transition");
        var (s3, v3, _) = await admin.PostAsync<EpisodeDetail>($"/api/v1/episodes/{id}/ack", new AckRequest(Force: true), ifMatch: v2.Item.Version);
        s3.Should().Be(HttpStatusCode.OK);
        v3!.Item.Assignee!.Username.Should().Be("admin");

        var (s4, v4, _) = await admin.PostAsync<EpisodeDetail>($"/api/v1/episodes/{id}/assign", new AssignRequest(null, _api.OperatorId), ifMatch: v3.Item.Version);
        s4.Should().Be(HttpStatusCode.OK);
        v4!.Item.Assignee!.Username.Should().Be("operator");

        await using var db = PostgresFixture.CreateContext(_api.ConnectionString);
        (await db.EpisodeEvents.CountAsync(e => e.EpisodeId == id && e.Kind == EpisodeEventKind.Takeover)).Should().Be(1);
        (await db.AuditEntries.CountAsync(a => a.TargetId == id.ToString() && a.Action == "episode.takeover")).Should().Be(1);
    }

    [Fact]
    public async Task Viewer_can_read_but_not_act()
    {
        var id = await _api.OpenEpisodeAsync("view-1");
        using var viewer = await _api.LoginAsync("viewer");
        (await viewer.GetAsync<EpisodeDetail>($"/api/v1/episodes/{id}"))!.Item.Id.Should().Be(id);
        using var ack = await viewer.RawAsync(HttpMethod.Post, $"/api/v1/episodes/{id}/ack", new AckRequest(), ifMatch: 1);
        ack.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var raw = await viewer.RawAsync(HttpMethod.Get, $"/api/v1/episodes/{id}/raw/{Guid.NewGuid()}");
        raw.StatusCode.Should().Be(HttpStatusCode.Forbidden, "raw payload needs integration_admin");
    }

    [Fact]
    public async Task Scenario_ManualCloseWhileSourceIsFiring_api_half_close_requires_reason_and_keeps_condition_then_restore_works()
    {
        var id = await _api.OpenEpisodeAsync("close-1");
        using var op = await _api.LoginAsync("operator");
        using var noReason = await op.RawAsync(HttpMethod.Post, $"/api/v1/episodes/{id}/close", new CloseRequest(""), ifMatch: 1);
        noReason.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var (s, closed, _) = await op.PostAsync<EpisodeDetail>($"/api/v1/episodes/{id}/close", new CloseRequest("known issue, ticket OPS-42"), ifMatch: 1);
        s.Should().Be(HttpStatusCode.OK);
        closed!.Item.HandlingState.Should().Be("closed");
        closed.Item.ConditionState.Should().Be("firing", "manual close never rewrites the last known condition");
        closed.Closure!.Reason.Should().Be("manual_close");
        closed.Closure.Evidence.Should().Be("human");
        closed.Closure.Note.Should().Be("known issue, ticket OPS-42");
        closed.Explanation.Should().Contain("Closed as manual_close");

        var closedView = await op.GetAsync<PagedResponse<EpisodeListItem>>("/api/v1/episodes?view=closed");
        closedView!.Items.Should().Contain(i => i.Id == id);
        (await op.GetAsync<PagedResponse<EpisodeListItem>>("/api/v1/episodes?view=needsAttention"))!.Items.Should().NotContain(i => i.Id == id);

        // The source keeps firing (a later signal): a new episode opens, linked to the closed one.
        _api.Time.Advance(TimeSpan.FromMinutes(1));
        var next = await _api.OpenEpisodeAsync("close-1");
        next.Should().NotBe(id);
        var related = await op.GetAsync<RelatedEpisodes>($"/api/v1/episodes/{next}");
        var nextDetail = await op.GetAsync<EpisodeDetail>($"/api/v1/episodes/{next}");
        nextDetail!.PreviousEpisodeId.Should().Be(id);
        _ = related;

        // The superseded episode cannot be restored while its successor is open (one open episode per identity).
        using var superseded = await op.RawAsync(HttpMethod.Post, $"/api/v1/episodes/{id}/restore", new RestoreRequest("review"), ifMatch: closed.Item.Version);
        superseded.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await superseded.Content.ReadAsStringAsync()).Should().Contain("invalid-transition").And.Contain(next.ToString());

        // Restore proper: close the successor manually, then bring it back for review.
        var (cs, closedNext, _) = await op.PostAsync<EpisodeDetail>($"/api/v1/episodes/{next}/close", new CloseRequest("closing to test restore"), ifMatch: nextDetail.Item.Version);
        cs.Should().Be(HttpStatusCode.OK);
        var (rs, restored, _) = await op.PostAsync<EpisodeDetail>($"/api/v1/episodes/{next}/restore", new RestoreRequest("review"), ifMatch: closedNext!.Item.Version);
        rs.Should().Be(HttpStatusCode.OK);
        restored!.Item.HandlingState.Should().Be("acknowledged");
        restored.Item.ConditionState.Should().Be("firing", "restore never rewrites the last known condition");
        restored.Closure.Should().BeNull();
        var timeline = await op.GetAsync<PagedResponse<TimelineEntry>>($"/api/v1/episodes/{next}/timeline");
        timeline!.Items.Should().Contain(e => e.Kind == "restore");
    }

    [Fact]
    public async Task Bulk_acknowledges_up_to_200_with_per_item_results()
    {
        var ids = new List<Guid> { await _api.OpenEpisodeAsync("bulk-1"), await _api.OpenEpisodeAsync("bulk-2"), await _api.OpenEpisodeAsync("bulk-b", _api.IntegrationB) };
        using var op = await _api.LoginAsync("operator");
        var (status, result, raw) = await op.PostAsync<BulkResponse>("/api/v1/episodes/bulk", new BulkRequest(ids.Concat([Guid.NewGuid()]).ToList(), "ack", null));
        status.Should().Be(HttpStatusCode.OK, raw);
        result!.Results.Should().HaveCount(4);
        result.Results.Where(r => ids.Take(2).Contains(r.Id)).Should().OnlyContain(r => r.Ok);
        result.Results.Single(r => r.Id == ids[2]).Problem!.Status.Should().Be(404, "scope-b is not visible");
        result.Results[^1].Problem!.Status.Should().Be(404);

        using var tooMany = await op.RawAsync(HttpMethod.Post, "/api/v1/episodes/bulk", new BulkRequest(Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()).ToList(), "ack", null));
        tooMany.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Silence_and_refresh_state_and_team_overview()
    {
        var id = await _api.OpenEpisodeAsync("sil-1");
        using var op = await _api.LoginAsync("operator");
        var (s, silenced, raw) = await op.PostAsync<EpisodeDetail>($"/api/v1/episodes/{id}/silence", new SilenceRequest(ApiFixture.T0.AddHours(2), "maintenance"), ifMatch: 1);
        s.Should().Be(HttpStatusCode.OK, raw);
        silenced!.Item.SuppressedUntil.Should().Be(ApiFixture.T0.AddHours(2));
        (await op.GetAsync<PagedResponse<EpisodeListItem>>("/api/v1/episodes?view=suppressed"))!.Items.Should().Contain(i => i.Id == id);
        using var tooLong = await op.RawAsync(HttpMethod.Post, $"/api/v1/episodes/{id}/silence", new SilenceRequest(ApiFixture.T0.AddDays(3), "long"), ifMatch: silenced.Item.Version);
        tooLong.StatusCode.Should().Be(HttpStatusCode.Forbidden, "> 24 h needs integration_admin");

        using var refresh = await op.RawAsync(HttpMethod.Post, $"/api/v1/episodes/{id}/refresh-state");
        refresh.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await using var db = PostgresFixture.CreateContext(_api.ConnectionString);
        (await db.Jobs.CountAsync(j => j.EpisodeId == id && j.Kind == "verify_state")).Should().Be(1);

        var overview = await op.GetAsync<TeamOverview>($"/api/v1/teams/{_api.TeamA}/overview");
        overview!.Team.Name.Should().Be("team-a");
        overview.OpenTotal.Should().BeGreaterThanOrEqualTo(1);
        var teams = await op.GetAsync<List<TeamSummary>>("/api/v1/teams");
        teams!.Should().ContainSingle(t => t.IsTriage);
    }
}
