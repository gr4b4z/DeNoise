using System.Text;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Auth;
using DeNoise.Application.Ingest;
using DeNoise.Application.Integrations;
using DeNoise.Application.Processing;
using DeNoise.Application.Teams;
using DeNoise.Domain.Users;
using DeNoise.Integration.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DeNoise.Integration.Tests.Milestone4;

/// <summary>An API host over a fresh database with an admin, an operator (scope A), a viewer and a scope-B user, plus a generic integration per scope.</summary>
public sealed class ApiFixture : IAsyncDisposable
{
    public static readonly DateTimeOffset T0 = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    public const string AdminPassword = "Bootstrap-Admin-Passw0rd!";
    public const string UserPassword = "Operator-Passw0rd-2026!";

    public HostFactory<DeNoise.Api.ApiHost> Factory { get; private set; } = null!;
    public FakeTimeProvider Time { get; } = new(T0);
    public string ConnectionString { get; private set; } = string.Empty;
    public Guid TeamA { get; private set; }
    public Guid OperatorId { get; private set; }
    public IntegrationCredentials IntegrationA { get; private set; } = null!;
    public IntegrationCredentials IntegrationB { get; private set; } = null!;

    public static async Task<ApiFixture> CreateAsync(PostgresFixture postgres, string name, IDictionary<string, string?>? settings = null)
    {
        var f = new ApiFixture { ConnectionString = await postgres.CreateDatabaseAsync(name) };
        var all = new Dictionary<string, string?>
        {
            ["Auth:Local:BootstrapPassword"] = AdminPassword,
            ["Notifications:AllowInsecureDestinations"] = "true",
            ["Limits:LoginPerMinutePerIp"] = "1000",
        };
        if (settings is not null) foreach (var (k, v) in settings) all[k] = v;
        f.Factory = new HostFactory<DeNoise.Api.ApiHost>(f.ConnectionString, f.Time, settings: all);
        _ = f.Factory.Server; // start the host (runs the bootstrap)

        await using var scope = f.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var admin = await sp.GetRequiredService<IUserRepository>().FindByUsernameAsync("admin") ?? throw new InvalidOperationException("bootstrap admin missing");
        var adminPrincipal = DeNoisePrincipal.ForSession(admin, "setup");
        var users = sp.GetRequiredService<UserService>();
        var (op, _) = await users.CreateAsync(new CreateUser("operator", "Olga Operator", "olga@example.test", [Roles.Operator], ["scope-a"]), adminPrincipal, "setup");
        var (viewer, _) = await users.CreateAsync(new CreateUser("viewer", "Vik Viewer", null, [Roles.Viewer], ["scope-a"]), adminPrincipal, "setup");
        var (other, _) = await users.CreateAsync(new CreateUser("other", "Otto Other", null, [Roles.Operator], ["scope-b"]), adminPrincipal, "setup");
        f.OperatorId = op.UserId;
        var hasher = sp.GetRequiredService<ISecretHasher>();
        foreach (var u in new[] { op, viewer, other })
        {
            u.PasswordHash = hasher.HashPassword(UserPassword);
            u.MustChangePassword = false;
        }
        await sp.GetRequiredService<IUnitOfWork>().CommitAsync();

        var team = await sp.GetRequiredService<TeamService>().CreateAsync(new CreateTeam("team-a", ["scope-a"], IsTriage: true), Actor.System("setup"));
        f.TeamA = team.TeamId;
        sp.GetRequiredService<ITeamMemberRepository>().Add(new TeamMember { TeamId = team.TeamId, UserId = op.UserId });
        await sp.GetRequiredService<IUnitOfWork>().CommitAsync();

        var integrations = sp.GetRequiredService<IntegrationService>();
        f.IntegrationA = await integrations.CreateAsync(new CreateIntegration("gen-a", "generic_webhook", "scope-a", team.TeamId), Actor.System("setup"));
        f.IntegrationB = await integrations.CreateAsync(new CreateIntegration("gen-b", "generic_webhook", "scope-b"), Actor.System("setup"));
        return f;
    }

    public ApiClient Client() => new(Factory.CreateClient());

    public async Task<ApiClient> LoginAsync(string username, string password = UserPassword)
    {
        var client = Client();
        var response = await client.LoginAsync(username, password);
        if (response.StatusCode != System.Net.HttpStatusCode.NoContent) throw new InvalidOperationException($"login failed: {response.StatusCode}");
        return client;
    }

    /// <summary>Ingests and processes one firing event through the real pipeline; returns the episode id.</summary>
    public async Task<Guid> OpenEpisodeAsync(string alertId, IntegrationCredentials? integration = null, string severity = "high", string? environment = "production")
    {
        integration ??= IntegrationA;
        var environmentField = environment is null ? string.Empty : $"\"environment\":\"{environment}\",";
        var body = $$"""{"eventType":"firing","alertId":"{{alertId}}","eventId":"{{Guid.NewGuid()}}","occurredAt":"{{Time.GetUtcNow():O}}","severity":"{{severity}}",{{environmentField}}"service":"orders","resource":{"id":"res-{{alertId}}","name":"Orders"},"rule":{"id":"5xx","name":"5xx rate"},"summary":"5xx high for {{alertId}}"}""";
        await using var scope = Factory.Services.CreateAsyncScope();
        var accepted = await scope.ServiceProvider.GetRequiredService<IngestService>().AcceptAsync(integration.Integration,
            new IngestRequest(Encoding.UTF8.GetBytes(body), "application/json", new Dictionary<string, string>(), null));
        var result = await scope.ServiceProvider.GetRequiredService<EventProcessor>().ProcessAsync(new NormaliseJobPayload(accepted.EventId, accepted.ReceivedAt, integration.Integration.IntegrationId));
        return result.EpisodeId ?? throw new InvalidOperationException($"processing outcome {result.Outcome}: {result.Detail}");
    }

    public async ValueTask DisposeAsync() => await Factory.DisposeAsync();
}
