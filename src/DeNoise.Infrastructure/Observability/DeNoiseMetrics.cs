using System.Diagnostics.Metrics;

namespace DeNoise.Infrastructure.Observability;

/// <summary>
/// Metric instruments whose names are the observability contract (ADR-9). Observable gauges are fed by
/// <see cref="IGaugeSource"/> implementations registered by the hosts that own the data.
/// </summary>
public sealed class DeNoiseMetrics : IDisposable
{
    public const string MeterName = "DeNoise";
    private readonly Meter _meter = new(MeterName);

    public DeNoiseMetrics()
    {
        IngestAccepted = _meter.CreateCounter<long>("denoise_ingest_accepted_total");
        IngestRejected = _meter.CreateCounter<long>("denoise_ingest_rejected_total");
        EpisodeTransitions = _meter.CreateCounter<long>("denoise_episode_transitions_total");
        Closures = _meter.CreateCounter<long>("denoise_closure_total");
        DeliveryAttempts = _meter.CreateCounter<long>("denoise_delivery_attempts_total");
        MappingFailures = _meter.CreateCounter<long>("denoise_mapping_failures_total");
        NotificationLatency = _meter.CreateHistogram<double>("denoise_notification_latency_seconds", unit: "s");
        SchedulerLag = _meter.CreateHistogram<double>("denoise_scheduler_lag_seconds", unit: "s");
    }

    public Counter<long> IngestAccepted { get; }
    public Counter<long> IngestRejected { get; }
    public Counter<long> EpisodeTransitions { get; }
    public Counter<long> Closures { get; }
    public Counter<long> DeliveryAttempts { get; }
    public Counter<long> MappingFailures { get; }
    public Histogram<double> NotificationLatency { get; }
    public Histogram<double> SchedulerLag { get; }

    /// <summary>Registers an observable gauge; the callback runs on every metrics collection.</summary>
    public void RegisterGauge(string name, Func<IEnumerable<Measurement<long>>> observe, string? unit = null)
        => _meter.CreateObservableGauge(name, observe, unit);

    public void RegisterGauge(string name, Func<IEnumerable<Measurement<double>>> observe, string? unit = null)
        => _meter.CreateObservableGauge(name, observe, unit);

    public void Dispose() => _meter.Dispose();
}

/// <summary>Names of the observable gauges from ADR-9, so hosts register them consistently.</summary>
public static class GaugeNames
{
    public const string JobQueueDepth = "denoise_job_queue_depth";
    public const string JobOldestAgeSeconds = "denoise_job_oldest_age_seconds";
    public const string EpisodeOpen = "denoise_episode_open";
    public const string CoverageState = "denoise_coverage_state";
    public const string HeartbeatState = "denoise_heartbeat_state";
    public const string UnassignedOpen = "denoise_unassigned_open";
}
