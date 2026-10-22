using DeNoise.Domain.Alerts;
using DeNoise.Domain.Ops;

namespace DeNoise.Application.Ingest;

/// <summary>Persists the raw event and its processing job in one committed transaction (AGENTS.md rule 3).</summary>
public interface IIngestStore
{
    Task AcceptAsync(RawEvent rawEvent, Job normaliseJob, CancellationToken ct = default);
}
