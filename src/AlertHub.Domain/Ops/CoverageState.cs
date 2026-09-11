namespace AlertHub.Domain.Ops;

/// <summary>Coverage states of one integration (04 §9): whether the Hub can trust silence from the source.</summary>
public static class CoverageStates
{
    public const string Unknown = "unknown";
    public const string Healthy = "healthy";
    public const string Delayed = "delayed";
    public const string Degraded = "degraded";
    public const string Unavailable = "unavailable";
    public const string NotConfigured = "not_configured";

    /// <summary>States in which silence has a competing explanation: closure by inactivity is suspended (spec §12.4).</summary>
    public static bool IsLost(string state) => state is Degraded or Unavailable;
}

/// <summary>Per-integration coverage record (<c>ops.coverage_state</c>). Milestone 5 drives the state machine; the read model already joins it.</summary>
public sealed class CoverageState
{
    public required Guid IntegrationId { get; init; }
    public string State { get; set; } = CoverageStates.Unknown;
    public DateTimeOffset Since { get; set; }
    public DateTimeOffset? LastSignalAt { get; set; }
    public int ConsecutiveSuccesses { get; set; }
    public DateTimeOffset? LastProcessedAlertAt { get; set; }
    public Guid? CoverageEpisodeId { get; set; }
    public string? Detail { get; set; }
}
