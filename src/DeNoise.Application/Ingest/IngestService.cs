using System.Text.Json;
using DeNoise.Application.Abstractions;
using DeNoise.Domain.Alerts;
using DeNoise.Domain.Common;
using DeNoise.Domain.Integrations;
using DeNoise.Domain.Ops;

namespace DeNoise.Application.Ingest;

public sealed record IngestRequest(byte[] Body, string? ContentType, IReadOnlyDictionary<string, string> Headers, System.Net.IPAddress? SourceIp);
public sealed record IngestAccepted(Guid EventId, DateTimeOffset ReceivedAt);

/// <summary>
/// Durable acceptance (spec §15.1): store bytes as-is, enqueue <c>normalise</c>, commit, and only then acknowledge.
/// Nothing here parses or maps the payload; that is the processing worker's job.
/// </summary>
public sealed class IngestService(IIngestStore store, TimeProvider time)
{
    public async Task<IngestAccepted> AcceptAsync(Integration integration, IngestRequest request, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var eventId = Ids.New(time);
        var raw = new RawEvent
        {
            EventId = eventId,
            IntegrationId = integration.IntegrationId,
            ReceivedAt = now,
            ContentType = request.ContentType,
            Body = request.Body,
            Headers = request.Headers.Count == 0 ? null : JsonSerializer.Serialize(request.Headers, JsonDefaults.Stored),
            SourceIp = request.SourceIp,
            SizeBytes = request.Body.Length,
        };
        var job = new Job
        {
            JobId = Ids.New(time),
            Kind = JobKinds.Normalise,
            NotBefore = now,
            Payload = JsonSerializer.Serialize(new NormaliseJobPayload(eventId, now, integration.IntegrationId), JsonDefaults.Stored),
            IntegrationId = integration.IntegrationId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await store.AcceptAsync(raw, job, ct);
        return new IngestAccepted(eventId, now);
    }
}

/// <summary>Payload of a <c>normalise</c> job; carries the partition key so the raw row is a single-partition lookup.</summary>
public sealed record NormaliseJobPayload(Guid EventId, DateTimeOffset ReceivedAt, Guid IntegrationId, bool IsReplay = false, int? MappingVersion = null);
