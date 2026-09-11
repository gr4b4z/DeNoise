using AlertHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlertHub.Workers.Scheduling;

public sealed class DeadmanOptions
{
    public const string Section = "AlertHub";
    /// <summary>External dead-man's switch URL (Helm <c>externalDeadmanUrl</c>, spec §13.7). Empty disables the job.</summary>
    public string? ExternalDeadmanUrl { get; set; }
    public TimeSpan DeadmanInterval { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan DeadmanTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// <c>deadman_ping</c> (04 §11, spec §13.7): the scheduler pings an external dead-man's switch every minute, but only after
/// proving the database answers — so the ping means "scheduler and database work", and its absence is reported by a system
/// that does not depend on Alert Hub. The last successful ping is recorded as component <c>deadman</c> for the hub health screen.
/// </summary>
public sealed class DeadmanPingWorker(IServiceScopeFactory scopes, IHttpClientFactory httpClients, IOptions<DeadmanOptions> options, TimeProvider time, ILogger<DeadmanPingWorker> logger) : BackgroundService
{
    public const string Component = "deadman";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var url = options.Value.ExternalDeadmanUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            logger.LogWarning("No AlertHub:ExternalDeadmanUrl configured: Alert Hub cannot report its own scheduler failure (spec §13.7 launch blocker)");
            return;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target) || (target.Scheme != Uri.UriSchemeHttps && target.Scheme != Uri.UriSchemeHttp))
        {
            logger.LogError("AlertHub:ExternalDeadmanUrl '{Url}' is not an absolute http(s) URL; dead-man ping disabled", url);
            return;
        }
        using var timer = new PeriodicTimer(options.Value.DeadmanInterval, time);
        do
        {
            try
            {
                await PingOnceAsync(target, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "deadman_ping failed; the external switch will notice if this persists");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<bool> PingOnceAsync(Uri target, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AlertHubDbContext>();
        // Only a live database earns a ping: a scheduler that runs but cannot read state must look dead from outside.
        if (!await db.Database.CanConnectAsync(ct)) throw new InvalidOperationException("database unreachable; dead-man ping withheld");

        using var client = httpClients.CreateClient(Component);
        client.Timeout = options.Value.DeadmanTimeout;
        using var response = await client.GetAsync(target, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("deadman_ping: {Status} from {Host}", (int)response.StatusCode, target.Host);
            return false;
        }
        var now = time.GetUtcNow();
        var instance = target.Host;
        var existing = await db.HubComponentHeartbeats.FindAsync([Component], ct);
        if (existing is null)
        {
            db.HubComponentHeartbeats.Add(new Domain.Ops.HubComponentHeartbeat { Component = Component, Instance = instance, LastSeen = now });
        }
        else
        {
            existing.Instance = instance;
            existing.LastSeen = now;
        }
        await db.SaveChangesAsync(ct);
        return true;
    }
}
