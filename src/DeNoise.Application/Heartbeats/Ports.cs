using DeNoise.Domain.Heartbeats;

namespace DeNoise.Application.Heartbeats;

/// <summary>Ids of rows the Hub creates for itself.</summary>
public static class WellKnownIntegrations
{
    /// <summary>The inactive system integration that owns heartbeat miss episodes not bound to a source integration (episodes need an integration row).</summary>
    public static readonly Guid Heartbeats = Guid.Parse("00000000-0000-0000-0000-0000000000b8");
}

public sealed record HeartbeatFilter(string? State = null, Guid? TeamId = null, string? Scope = null, Guid? IntegrationId = null, string? Query = null);

public interface IHeartbeatRepository
{
    Task<Heartbeat?> GetAsync(Guid heartbeatId, CancellationToken ct = default);
    Task<Heartbeat?> FindByKeyIdAsync(string keyId, CancellationToken ct = default);
    Task<IReadOnlyList<Heartbeat>> ListAsync(HeartbeatFilter filter, CancellationToken ct = default);
    Task<IReadOnlyList<Heartbeat>> ListBoundToAsync(Guid integrationId, CancellationToken ct = default);
    Task<IReadOnlyList<HeartbeatRun>> ListRunsAsync(Guid heartbeatId, int limit, CancellationToken ct = default);
    void Add(Heartbeat heartbeat);
    void Remove(Heartbeat heartbeat);
}

/// <summary>Maintenance windows (spec §16.3, milestone 9). Until then nothing is ever covered.</summary>
public interface IMaintenanceWindows
{
    Task<bool> CoversAsync(string accessScope, DateTimeOffset now, CancellationToken ct = default);
}

public sealed class NoMaintenanceWindows : IMaintenanceWindows
{
    public Task<bool> CoversAsync(string accessScope, DateTimeOffset now, CancellationToken ct = default) => Task.FromResult(false);
}

/// <summary>Resolves a heartbeat from the URL credential <c>{keyId}.{token}</c>; unknown key and wrong token are indistinguishable (404, constant-time).</summary>
public interface IHeartbeatPingAuthenticator
{
    Task<Heartbeat?> AuthenticateAsync(string keyId, string token, CancellationToken ct = default);
}

public sealed class HeartbeatOptions
{
    public const string Section = "Heartbeats";
    /// <summary>Public base URL of the ingest host, used to build ping URLs (Helm <c>ingestPublicBaseUrl</c>).</summary>
    public string IngestPublicBaseUrl { get; set; } = "http://localhost:8081";
    /// <summary>Ring size of stored runs per heartbeat (spec §13.3.3 proposes 100).</summary>
    public int RunRingSize { get; set; } = 100;
    /// <summary>Stored ping body limit; longer bodies keep their tail (06 §3: ≤ 16 KiB).</summary>
    public int BodyBytes { get; set; } = 16 * 1024;
    public int PingsPerMinutePerKey { get; set; } = 60;
}
