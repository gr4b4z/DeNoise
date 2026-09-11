using AlertHub.Domain.Integrations;

namespace AlertHub.Application.Integrations;

public interface IIntegrationRepository
{
    /// <summary>Current (non-deactivated) version by id.</summary>
    Task<Integration?> GetCurrentAsync(Guid integrationId, CancellationToken ct = default);

    /// <summary>Current version whose ingest key matches; used on every ingest request.</summary>
    Task<Integration?> FindCurrentByIngestKeyAsync(string ingestKeyId, CancellationToken ct = default);

    Task<IReadOnlyList<Integration>> ListCurrentAsync(CancellationToken ct = default);

    void Add(Integration version);
}
