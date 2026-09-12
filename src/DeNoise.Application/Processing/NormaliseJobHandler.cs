using System.Text.Json;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Ingest;
using DeNoise.Application.Ops;
using DeNoise.Domain.Ops;

namespace DeNoise.Application.Processing;

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
