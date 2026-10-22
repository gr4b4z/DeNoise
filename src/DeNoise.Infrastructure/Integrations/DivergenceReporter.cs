using System.Diagnostics;
using System.Text.Json;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Divergence;
using DeNoise.Application.Integrations;
using DeNoise.Application.Processing;
using DeNoise.Domain.Audit;
using DeNoise.Domain.Common;
using DeNoise.Domain.Episodes;
using DeNoise.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeNoise.Infrastructure.Integrations;

/// <summary>Runs the divergence comparison for one integration and records it as an <c>integration.divergence_report</c> audit entry (the last one is what the UI shows).</summary>
public sealed class DivergenceReporter(DeNoiseDbContext db, IIntegrationRepository integrations, IStateQueryAdapter adapter, IOptions<PilotOptions> options, TimeProvider time, ILogger<DivergenceReporter> logger) : IDivergenceReporter
{
    public const string Action = "integration.divergence_report";

    public async Task<DivergenceReport> RunAsync(Guid integrationId, CancellationToken ct = default)
    {
        var integration = await integrations.GetCurrentAsync(integrationId, ct) ?? throw new KeyNotFoundException($"Integration {integrationId} not found.");
        var now = time.GetUtcNow();
        var watch = Stopwatch.StartNew();
        var o = options.Value;
        var open = await db.Episodes.AsNoTracking().Where(e => e.IntegrationId == integrationId && e.HandlingState != HandlingState.Closed && e.IsActionable).OrderByDescending(e => e.LastSeen).ToListAsync(ct);

        int agree = 0, diverged = 0, unknown = 0;
        var supported = true;
        string? detail = null;
        var samples = new List<DivergenceSample>();
        if (open.Count == 0)
        {
            // Nothing to compare: the probe still tells whether this source could answer at all (spec §13.5 api_probe).
            var probe = await adapter.ProbeAsync(integration, ct);
            supported = probe.Supported;
            detail = supported ? "no open episodes to compare yet" : probe.Detail ?? "the source has no state-query API for this integration";
        }
        foreach (var episode in open)
        {
            StateQueryResult result;
            try
            {
                result = await adapter.QueryAsync(integration, episode, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = new StateQueryResult(StateQueryOutcome.Error, ex.Message);
            }
            switch (result.Outcome)
            {
                case StateQueryOutcome.Unsupported:
                    supported = false;
                    detail ??= result.Detail ?? "the source has no state-query API for this integration; compare against the source manually (pilot runbook, shadow checklist)";
                    break;
                case StateQueryOutcome.Active:
                    agree++;
                    break;
                case StateQueryOutcome.NotActive:
                    diverged++;
                    if (samples.Count < o.SampleSize) samples.Add(new DivergenceSample(episode.EpisodeId, episode.Summary, episode.Severity.ToString().ToLowerInvariant(), episode.LastSeen, "diverged", result.Detail));
                    break;
                default:
                    unknown++;
                    if (samples.Count < o.SampleSize) samples.Add(new DivergenceSample(episode.EpisodeId, episode.Summary, episode.Severity.ToString().ToLowerInvariant(), episode.LastSeen, "unknown", result.Detail));
                    break;
            }
            if (!supported) break;
        }
        watch.Stop();
        var answered = agree + diverged;
        double? share = supported && answered > 0 ? (double)diverged / answered : null;
        bool? within = share is null ? null : share <= o.Threshold();
        var report = new DivergenceReport(integrationId, now, supported, detail, open.Count, agree, diverged, unknown, share, o.DivergenceThreshold, within, samples, watch.ElapsedMilliseconds);

        db.AuditEntries.Add(new AuditEntry
        {
            Id = Ids.New(time),
            At = now,
            ActorType = ActorTypes.System,
            ActorId = "divergence",
            ActorDisplay = "DeNoise divergence check",
            Action = Action,
            TargetType = "integration",
            TargetId = integrationId.ToString(),
            AccessScope = integration.AccessScope,
            After = JsonSerializer.Serialize(report, JsonDefaults.Stored),
            CorrelationId = Ids.New(time).ToString("N"),
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("divergence {Integration}: {Open} open, {Agree} agree, {Diverged} diverged, {Unknown} unknown (supported: {Supported})", integration.Name, open.Count, agree, diverged, unknown, supported);
        return report;
    }

    public async Task<DivergenceReport?> LastAsync(Guid integrationId, CancellationToken ct = default)
    {
        var id = integrationId.ToString();
        var json = await db.AuditEntries.AsNoTracking().Where(a => a.Action == Action && a.TargetId == id).OrderByDescending(a => a.At).Select(a => a.After).FirstOrDefaultAsync(ct);
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<DivergenceReport>(json, JsonDefaults.Stored); }
        catch (JsonException) { return null; }
    }
}

file static class PilotOptionsExtensions
{
    public static double Threshold(this PilotOptions o) => Math.Clamp(o.DivergenceThreshold, 0, 1);
}
