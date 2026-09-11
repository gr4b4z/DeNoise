using AlertHub.Application.Abstractions;
using AlertHub.Application.Integrations;
using AlertHub.Application.Ops;
using AlertHub.Domain.Integrations;
using AlertHub.Infrastructure;
using AlertHub.Infrastructure.Observability;
using AlertHub.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlertHub.Integration.Tests.Support;

/// <summary>A DI container with the real persistence and infrastructure services over a test database, without any host.</summary>
public static class TestServices
{
    public static ServiceProvider Build(string connectionString, TimeProvider? time = null, Action<IServiceCollection>? configure = null, IDictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(time ?? TimeProvider.System);
        services.AddSingleton<AlertHubMetrics>();
        services.AddAlertHubPersistence(connectionString);
        services.AddAlertHubInfrastructure(configuration);
        configure?.Invoke(services);
        return services.BuildServiceProvider(validateScopes: true);
    }

    public static async Task<IntegrationCredentials> CreateIntegrationAsync(IServiceProvider provider, string name = "test", string type = IntegrationTypes.GenericWebhook, string scope = "test-scope")
    {
        await using var s = provider.CreateAsyncScope();
        var service = s.ServiceProvider.GetRequiredService<IntegrationService>();
        return await service.CreateAsync(new CreateIntegration(name, type, scope), Actor.System("test"));
    }

    public static async Task<T> InScopeAsync<T>(IServiceProvider provider, Func<IServiceProvider, Task<T>> action)
    {
        await using var s = provider.CreateAsyncScope();
        return await action(s.ServiceProvider);
    }

    public static Task InScopeAsync(IServiceProvider provider, Func<IServiceProvider, Task> action)
        => InScopeAsync(provider, async sp => { await action(sp); return 0; });

    public static Task<T> InQueueAsync<T>(IServiceProvider provider, Func<IJobQueue, Task<T>> action)
        => InScopeAsync(provider, sp => action(sp.GetRequiredService<IJobQueue>()));

    public static Task<T> InDbAsync<T>(IServiceProvider provider, Func<AlertHubDbContext, Task<T>> action)
        => InScopeAsync(provider, sp => action(sp.GetRequiredService<AlertHubDbContext>()));
}
