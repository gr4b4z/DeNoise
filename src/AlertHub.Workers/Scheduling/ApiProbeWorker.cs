using AlertHub.Application.Coverage;
using AlertHub.Application.Integrations;
using AlertHub.Application.Processing;

namespace AlertHub.Workers.Scheduling;

/// <summary>
/// <c>api_probe</c> (spec §13.5): for integrations whose coverage lists the method and whose type has a state-query adapter,
/// asks the source API whether it is reachable at the configured interval. A failed probe degrades coverage (explicit
/// failure); a successful one is logged only — reachability never proves that alerts are being evaluated.
/// </summary>
public sealed class ApiProbeWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<ApiProbeWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
    private readonly Dictionary<Guid, DateTimeOffset> _lastProbe = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var integrations = await scope.ServiceProvider.GetRequiredService<IIntegrationRepository>().ListCurrentAsync(stoppingToken);
                var adapter = scope.ServiceProvider.GetRequiredService<IStateQueryAdapter>();
                var coverage = scope.ServiceProvider.GetRequiredService<CoverageEvaluator>();
                var now = time.GetUtcNow();
                foreach (var integration in integrations.Where(i => i.Active))
                {
                    CoverageConfig config;
                    try
                    {
                        config = CoverageConfig.Parse(integration.Coverage);
                    }
                    catch (AlertHub.Application.Mapping.MappingValidationException)
                    {
                        continue;
                    }
                    if (config.ApiProbe is null) continue;
                    if (_lastProbe.TryGetValue(integration.IntegrationId, out var last) && now - last < config.ApiProbe.Interval) continue;
                    _lastProbe[integration.IntegrationId] = now;
                    var result = await adapter.ProbeAsync(integration, stoppingToken);
                    if (!result.Supported)
                    {
                        logger.LogDebug("api_probe for {IntegrationId}: {Detail}", integration.IntegrationId, result.Detail);
                        continue;
                    }
                    if (result.Ok)
                    {
                        logger.LogInformation("api_probe for {IntegrationId} ok: {Detail}", integration.IntegrationId, result.Detail);
                        continue;
                    }
                    logger.LogWarning("api_probe for {IntegrationId} failed: {Detail}", integration.IntegrationId, result.Detail);
                    await coverage.OnApiProbeAsync(integration, result, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "api_probe tick failed; will retry on next tick");
            }
        }
    }
}
