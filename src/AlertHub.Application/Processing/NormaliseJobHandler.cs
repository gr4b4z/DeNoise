using System.Text.Json;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Ingest;
using AlertHub.Application.Ops;
using AlertHub.Domain.Ops;

namespace AlertHub.Application.Processing;

/// <summary><c>normalise</c> (and replay-flagged) jobs → <see cref="EventProcessor"/>. Idempotent: the ledger stops re-runs.</summary>
public sealed class NormaliseJobHandler(EventProcessor processor) : IJobHandler
{
    public string Kind => JobKinds.Normalise;

    public async Task HandleAsync(Job job, JobContext context, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<NormaliseJobPayload>(job.Payload, JsonDefaults.Stored)
            ?? throw new InvalidOperationException($"job {job.JobId} has no payload");
        await processor.ProcessAsync(payload, ct);
    }
}
