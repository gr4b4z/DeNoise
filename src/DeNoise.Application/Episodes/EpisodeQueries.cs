using DeNoise.Application.Auth;
using DeNoise.Contracts;

namespace DeNoise.Application.Episodes;

/// <summary>Work-queue views (06 §4 <c>view=</c>).</summary>
public static class QueueViews
{
    public const string NeedsAttention = "needsAttention";
    public const string Mine = "mine";
    public const string MyTeams = "myTeams";
    public const string Unassigned = "unassigned";
    public const string Acknowledged = "acknowledged";
    public const string Stale = "stale";
    public const string Suppressed = "suppressed";
    public const string Closed = "closed";
    public static readonly IReadOnlyList<string> All = [NeedsAttention, Mine, MyTeams, Unassigned, Acknowledged, Stale, Suppressed, Closed];
}

/// <summary>Filters from the query string (06 §1). Scope enforcement is applied by the query from the principal, never from the request.</summary>
public sealed record EpisodeFilter(
    string? View = null, IReadOnlyList<string>? Severity = null, IReadOnlyList<string>? Handling = null, IReadOnlyList<string>? Condition = null,
    Guid? TeamId = null, string? Scope = null, string? Environment = null, string? Service = null, Guid? IntegrationId = null, string? Query = null,
    string? ClosureReason = null, string? Evidence = null, string? Sort = null, int Limit = 50, string? Cursor = null, bool IncludeTotal = false);

/// <summary>Hot read model (ADR-4): hand-written SQL in Infrastructure. Every method filters by the principal's scopes.</summary>
public interface IEpisodeQueries
{
    Task<PagedResponse<EpisodeListItem>> ListAsync(DeNoisePrincipal principal, EpisodeFilter filter, IReadOnlyCollection<Guid> myTeamIds, CancellationToken ct = default);
    Task<EpisodeDetail?> GetAsync(DeNoisePrincipal principal, Guid episodeId, CancellationToken ct = default);
    Task<PagedResponse<TimelineEntry>> TimelineAsync(DeNoisePrincipal principal, Guid episodeId, int limit, string? cursor, string? kind, CancellationToken ct = default);
    Task<RelatedEpisodes?> RelatedAsync(DeNoisePrincipal principal, Guid episodeId, CancellationToken ct = default);
    Task<(byte[] Body, string? ContentType, DateTimeOffset ReceivedAt)?> RawPayloadAsync(DeNoisePrincipal principal, Guid episodeId, Guid eventId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, int>> ViewCountsAsync(DeNoisePrincipal principal, IReadOnlyCollection<Guid> myTeamIds, CancellationToken ct = default);
    Task<TeamOverview?> TeamOverviewAsync(DeNoisePrincipal principal, Guid teamId, CancellationToken ct = default);
}
