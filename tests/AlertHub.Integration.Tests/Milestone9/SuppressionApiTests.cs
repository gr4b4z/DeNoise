using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlertHub.Contracts;
using AlertHub.Integration.Tests.Milestone4;
using AlertHub.Integration.Tests.Support;

namespace AlertHub.Integration.Tests.Milestone9;

/// <summary>06 §4 <c>/suppressions</c> (permissions: 24 h for operators, longer needs integration_admin), <c>/history</c> and <c>/groups</c> over HTTP.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SuppressionApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private ApiFixture _api = null!;

    public async Task InitializeAsync() => _api = await ApiFixture.CreateAsync(postgres, "suppressionapi");
    public async Task DisposeAsync() => await _api.DisposeAsync();

    private static JsonElement Scope(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Operators_create_short_silences_long_windows_need_integration_admin_and_cancel_ends_now()
    {
        using var op = await _api.LoginAsync("operator");
        var now = ApiFixture.T0;
        var (status, created, raw) = await op.PostAsync<SuppressionDto>("/api/v1/suppressions",
            new CreateSuppressionRequest("silence", Scope("""{ "eq": ["service", "orders"] }"""), "Europe/Warsaw", "deploy", StartsAt: now, EndsAt: now + TimeSpan.FromHours(2), Name: "orders deploy"));
        status.Should().Be(HttpStatusCode.Created, raw);
        created!.Active.Should().BeTrue();
        created.ScopeText.Should().Be("service is 'orders'");
        created.Window.Should().Be("2026-09-11 14:00 → 16:00 Europe/Warsaw");

        using var tooLong = await op.RawAsync(HttpMethod.Post, "/api/v1/suppressions", new CreateSuppressionRequest("maintenance", Scope("""{ "eq": ["access_scope", "scope-a"] }"""), "UTC", "long", StartsAt: now, EndsAt: now + TimeSpan.FromHours(48)));
        tooLong.StatusCode.Should().Be(HttpStatusCode.Forbidden, "windows longer than 24 h need suppression.create.long");

        using var noReason = await op.RawAsync(HttpMethod.Post, "/api/v1/suppressions", new CreateSuppressionRequest("silence", Scope("""{ "eq": ["service", "orders"] }"""), "UTC", "", StartsAt: now, EndsAt: now + TimeSpan.FromHours(1)));
        noReason.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await noReason.Content.ReadAsStringAsync()).Should().Contain("$.reason");

        using var badZone = await op.RawAsync(HttpMethod.Post, "/api/v1/suppressions", new CreateSuppressionRequest("silence", Scope("""{ "eq": ["service", "orders"] }"""), "Mars/Olympus", "x", StartsAt: now, EndsAt: now + TimeSpan.FromHours(1)));
        badZone.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var admin = await _api.LoginAsync("admin", ApiFixture.AdminPassword);
        using var change = await admin.RawAsync(HttpMethod.Post, "/auth/change-password", new ChangePasswordRequest(ApiFixture.AdminPassword, "Platform-admin-passphrase-9"), idempotency: false);
        var (longStatus, longWindow, longRaw) = await admin.PostAsync<SuppressionDto>("/api/v1/suppressions",
            new CreateSuppressionRequest("maintenance", Scope("""{ "eq": ["access_scope", "scope-a"] }"""), "Europe/Warsaw", "weekend patching", StartsLocal: new DateTime(2026, 9, 12, 22, 0, 0), EndsLocal: new DateTime(2026, 9, 14, 6, 0, 0)));
        longStatus.Should().Be(HttpStatusCode.Created, longRaw);
        longWindow!.Active.Should().BeFalse("starts tomorrow");
        longWindow.StartsAt.Should().Be(new DateTimeOffset(2026, 9, 12, 20, 0, 0, TimeSpan.Zero), "22:00 CEST");

        var list = await op.GetAsync<List<SuppressionDto>>("/api/v1/suppressions");
        list!.Select(s => s.Id).Should().BeEquivalentTo([created.Id, longWindow.Id]);

        using var cancel = await op.RawAsync(HttpMethod.Delete, "/api/v1/suppressions/" + created.Id);
        cancel.StatusCode.Should().Be(HttpStatusCode.OK);
        var ended = await cancel.Content.ReadFromJsonAsync<SuppressionEndedResponse>(ApiClient.Json);
        ended!.EpisodesReleased.Should().Be(0);
        (await op.GetAsync<List<SuppressionDto>>("/api/v1/suppressions"))!.Select(s => s.Id).Should().Equal(longWindow.Id);
        (await op.GetAsync<List<SuppressionDto>>("/api/v1/suppressions?all=true"))!.Should().HaveCount(2);
        using var cancelAgain = await op.RawAsync(HttpMethod.Delete, "/api/v1/suppressions/" + created.Id);
        cancelAgain.StatusCode.Should().Be(HttpStatusCode.NoContent, "already ended");
    }

    [Fact]
    public async Task History_lists_closed_episodes_with_closure_filters()
    {
        var open = await _api.OpenEpisodeAsync("h-open");
        var closed = await _api.OpenEpisodeAsync("h-closed");
        using var op = await _api.LoginAsync("operator");
        using var close = await op.RawAsync(HttpMethod.Post, $"/api/v1/episodes/{closed}/close", new CloseRequest("known issue"), ifMatch: 1);
        close.StatusCode.Should().Be(HttpStatusCode.OK, await close.Content.ReadAsStringAsync());

        var history = await op.GetAsync<PagedResponse<EpisodeListItem>>("/api/v1/history?includeTotal=true");
        history!.Items.Select(i => i.Id).Should().Equal(closed);
        history.Total.Should().Be(1);
        (await op.GetAsync<PagedResponse<EpisodeListItem>>("/api/v1/history?closureReason=manual_close"))!.Items.Should().ContainSingle(i => i.Id == closed);
        (await op.GetAsync<PagedResponse<EpisodeListItem>>("/api/v1/history?closureReason=source_resolved"))!.Items.Should().BeEmpty();
        (await op.GetAsync<PagedResponse<EpisodeListItem>>("/api/v1/episodes?view=needsAttention"))!.Items.Select(i => i.Id).Should().Equal(open);

        using var viewer = await _api.LoginAsync("viewer");
        (await viewer.GetAsync<PagedResponse<EpisodeListItem>>("/api/v1/history"))!.Items.Should().HaveCount(1, "viewers have history.read");
        using var group = await op.RawAsync(HttpMethod.Get, "/api/v1/groups/" + Guid.NewGuid());
        group.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
