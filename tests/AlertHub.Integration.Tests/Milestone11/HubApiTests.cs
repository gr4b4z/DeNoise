using System.Net;
using AlertHub.Contracts;
using AlertHub.Integration.Tests.Milestone4;
using AlertHub.Integration.Tests.Support;

namespace AlertHub.Integration.Tests.Milestone11;

/// <summary>06 §4 hub health, failure queue and retention over HTTP (08 §3.9 screen).</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class HubApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private ApiFixture _api = null!;

    public async Task InitializeAsync() => _api = await ApiFixture.CreateAsync(postgres, "hubapi");
    public async Task DisposeAsync() => await _api.DisposeAsync();

    [Fact]
    public async Task Health_failures_and_retention_follow_the_permission_matrix()
    {
        await _api.OpenEpisodeAsync("hub-1");
        using var op = await _api.LoginAsync("operator");
        var health = await op.GetAsync<HubHealth>("/api/v1/hub/health");
        health!.DatabaseOk.Should().BeTrue();
        health.Deadman.Configured.Should().BeFalse();
        health.Queues.Should().NotBeNull();

        using var failures = await op.RawAsync(HttpMethod.Get, "/api/v1/hub/failures", csrf: false, idempotency: false);
        failures.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the failure queue needs hub.admin");
        var status = await op.GetAsync<RetentionStatusDto>("/api/v1/hub/retention");
        status!.Settings.RawDays.Should().Be(30);
        status.PartitionCount.Should().BeGreaterThanOrEqualTo(9);
        status.LastRun.Should().BeNull("never ran");
        status.NextRunAt.Should().Be(new DateTimeOffset(2026, 9, 12, 2, 0, 0, TimeSpan.Zero));
        using var opRun = await op.RawAsync(HttpMethod.Post, "/api/v1/hub/retention/run");
        opRun.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var admin = await _api.LoginAsync("admin", ApiFixture.AdminPassword);
        using var change = await admin.RawAsync(HttpMethod.Post, "/auth/change-password", new ChangePasswordRequest(ApiFixture.AdminPassword, "Platform-admin-passphrase-9"), idempotency: false);
        change.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var list = await admin.GetAsync<List<HubFailure>>("/api/v1/hub/failures?limit=10");
        list.Should().NotBeNull();
        using var retryMissing = await admin.RawAsync(HttpMethod.Post, $"/api/v1/hub/failures/{Guid.NewGuid()}/retry");
        retryMissing.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var (runStatus, report, raw) = await admin.PostAsync<RetentionReportDto>("/api/v1/hub/retention/run");
        runStatus.Should().Be(HttpStatusCode.OK, raw);
        report!.DroppedPartitions.Should().BeEmpty();
        report.Deleted.Should().ContainKey("episode").WhoseValue.Should().Be(0);
        var after = await admin.GetAsync<RetentionStatusDto>("/api/v1/hub/retention");
        after!.LastRun.Should().NotBeNull();
        after.LastRun!.At.Should().Be(ApiFixture.T0);

        var audit = await admin.GetAsync<PagedResponse<AuditEntryDto>>("/api/v1/audit?action=retention.run");
        audit!.Items.Should().ContainSingle().Which.ActorDisplay.Should().Be("Alert Hub retention");
    }
}
