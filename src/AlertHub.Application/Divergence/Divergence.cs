namespace AlertHub.Application.Divergence;

/// <summary>Spec §21 shadow-mode gate: the share of open episodes whose source says "not active" must stay under this (<c>Pilot:DivergenceThreshold</c>).</summary>
public sealed class PilotOptions
{
    public const string Section = "Pilot";
    public double DivergenceThreshold { get; set; } = 0.05;
    /// <summary>How many divergent episodes the report lists by id.</summary>
    public int SampleSize { get; set; } = 20;
}

public sealed record DivergenceSample(Guid EpisodeId, string? Summary, string Severity, DateTimeOffset LastSeen, string Outcome, string? Detail);

/// <summary>
/// Hub state vs source state for one integration (09 M12, spec §21 "comparing Hub state against reality"): every open
/// episode is asked of the source through its <c>state_query</c> adapter; <c>agree</c> = source still active,
/// <c>diverged</c> = source says not active while the hub keeps it open, <c>unknown</c> = the source could not answer.
/// </summary>
public sealed record DivergenceReport(Guid IntegrationId, DateTimeOffset At, bool Supported, string? Detail, int OpenEpisodes, int Agree, int Diverged, int Unknown,
    double? DivergenceShare, double Threshold, bool? WithinThreshold, IReadOnlyList<DivergenceSample> Samples, long DurationMs);

public interface IDivergenceReporter
{
    Task<DivergenceReport> RunAsync(Guid integrationId, CancellationToken ct = default);
    Task<DivergenceReport?> LastAsync(Guid integrationId, CancellationToken ct = default);
}
