using System.Net;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Auth;
using AlertHub.Application.Heartbeats;
using AlertHub.Application.Teams;
using AlertHub.Domain.Heartbeats;
using AlertHub.Domain.Users;
using AlertHub.Integration.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AlertHub.Integration.Tests.Milestone6;

/// <summary>06 §3 ping contract on the ingest host: GET and POST succeed, unknown key and wrong token are both 404, per-key 429.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class HeartbeatPingEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private string _cs = string.Empty;
    private HostFactory<AlertHub.Ingest.IngestHost> _factory = null!;
    private string _pingUrl = string.Empty;
    private Guid _heartbeatId;

    public async Task InitializeAsync()
    {
        _cs = await postgres.CreateDatabaseAsync("hbping");
        _factory = new HostFactory<AlertHub.Ingest.IngestHost>(_cs, new FakeTimeProvider(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero)), settings: new Dictionary<string, string?>
        {
            ["Heartbeats:PingsPerMinutePerKey"] = "3",
            ["Heartbeats:IngestPublicBaseUrl"] = "http://localhost",
        });
        await using var scope = _factory.Services.CreateAsyncScope();
        var team = await scope.ServiceProvider.GetRequiredService<TeamService>().CreateAsync(new CreateTeam("ops", ["scope-a"]), Actor.System("setup"));
        var principal = AlertHubPrincipal.ForSession(new User { UserId = Guid.NewGuid(), Username = "op", DisplayName = "Op", RoleNames = [Roles.Operator], Scopes = ["scope-a"] }, "s");
        var created = await scope.ServiceProvider.GetRequiredService<HeartbeatService>().CreateAsync(
            new HeartbeatDefinition("cron-job", null, team.TeamId, null, ScheduleKinds.Interval, TimeSpan.FromMinutes(5), null, null, TimeSpan.FromMinutes(1), "high", null, null), principal, "setup");
        _pingUrl = created.PingUrl;
        _heartbeatId = created.Heartbeat.HeartbeatId;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Get_and_post_pings_succeed_with_a_fixed_body_and_unknown_credentials_are_404()
    {
        using var client = _factory.CreateClient();
        using var get = await client.GetAsync(new Uri(_pingUrl));
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        (await get.Content.ReadAsStringAsync()).Should().Be("""{"ok":true}""");

        using var fail = await client.PostAsync(new Uri(_pingUrl + "/fail"), new StringContent("disk full"));
        fail.StatusCode.Should().Be(HttpStatusCode.OK);

        using var wrongToken = await client.GetAsync(new Uri(_pingUrl[.._pingUrl.LastIndexOf('.')] + ".not-the-secret-not-the-secret"));
        wrongToken.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var unknownKey = await client.GetAsync(new Uri("http://localhost/hb/abcdefghjkmn.whatever-token-value-here"));
        unknownKey.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using var scope = _factory.Services.CreateAsyncScope();
        var runs = await scope.ServiceProvider.GetRequiredService<HeartbeatService>().RunsAsync(_heartbeatId, 10);
        runs.Select(r => r.Kind).Should().BeEquivalentTo([RunKinds.Success, RunKinds.Fail]);
        runs.Single(r => r.Kind == RunKinds.Fail).Body.Should().Be("disk full");
    }

    [Fact]
    public async Task Pings_are_rate_limited_per_key()
    {
        using var client = _factory.CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            using var r = await client.GetAsync(new Uri(_pingUrl));
            statuses.Add(r.StatusCode);
        }
        statuses.Should().Contain(HttpStatusCode.TooManyRequests);
        statuses.Take(3).Should().OnlyContain(s => s == HttpStatusCode.OK);
    }
}
