namespace AlertHub.Domain.Users;

public static class Roles
{
    public const string Viewer = "viewer";
    public const string Operator = "operator";
    public const string IntegrationAdmin = "integration_admin";
    public const string PlatformAdmin = "platform_admin";
    public static readonly IReadOnlyList<string> All = [Viewer, Operator, IntegrationAdmin, PlatformAdmin];
}

/// <summary>Permission names (04 §8). Strings so they can double as PAT scopes and appear in OpenAPI.</summary>
public static class Permissions
{
    public const string EpisodeRead = "episode.read";
    public const string HistoryRead = "history.read";
    public const string EpisodeAck = "episode.ack";
    public const string EpisodeAssign = "episode.assign";
    public const string EpisodeNote = "episode.note";
    public const string EpisodeClose = "episode.close";
    public const string EpisodeRestore = "episode.restore";
    public const string EpisodeSilence = "episode.silence";
    public const string EpisodeBulk = "episode.bulk";
    public const string EpisodeRawPayloadRead = "episode.raw_payload.read";
    public const string HeartbeatManage = "heartbeat.manage";
    public const string IntegrationRead = "integration.read";
    public const string IntegrationManage = "integration.manage";
    public const string MappingManage = "mapping.manage";
    public const string ReplayPreview = "replay.preview";
    public const string ReplayRetry = "replay.retry";
    public const string ReplayHistorical = "replay.historical";
    public const string PolicyManage = "policy.manage";
    public const string TeamManage = "team.manage";
    public const string DestinationManage = "destination.manage";
    public const string SuppressionCreate = "suppression.create";
    public const string SuppressionCreateLong = "suppression.create.long";
    public const string AuditRead = "audit.read";
    public const string HubHealthRead = "hub.health.read";
    public const string HubAdmin = "hub.admin";
    public const string UserManage = "user.manage";
    public const string MeTokens = "me.tokens";
    public const string MeSessions = "me.sessions";

    public static readonly IReadOnlyList<string> All =
    [
        EpisodeRead, HistoryRead, EpisodeAck, EpisodeAssign, EpisodeNote, EpisodeClose, EpisodeRestore, EpisodeSilence, EpisodeBulk, EpisodeRawPayloadRead,
        HeartbeatManage, IntegrationRead, IntegrationManage, MappingManage, ReplayPreview, ReplayRetry, ReplayHistorical, PolicyManage, TeamManage,
        DestinationManage, SuppressionCreate, SuppressionCreateLong, AuditRead, HubHealthRead, HubAdmin, UserManage, MeTokens, MeSessions,
    ];
}

/// <summary>The action × role matrix of 04 §8. Every check also requires the target's access scope to be in the user's scopes.</summary>
public static class Rbac
{
    private static readonly Dictionary<string, HashSet<string>> Matrix = new(StringComparer.Ordinal)
    {
        [Roles.Viewer] = [Permissions.EpisodeRead, Permissions.HistoryRead, Permissions.IntegrationRead, Permissions.AuditRead, Permissions.MeTokens, Permissions.MeSessions],
        [Roles.Operator] =
        [
            Permissions.EpisodeRead, Permissions.HistoryRead, Permissions.EpisodeAck, Permissions.EpisodeAssign, Permissions.EpisodeNote, Permissions.EpisodeClose,
            Permissions.EpisodeRestore, Permissions.EpisodeSilence, Permissions.EpisodeBulk, Permissions.HeartbeatManage, Permissions.IntegrationRead,
            Permissions.SuppressionCreate, Permissions.AuditRead, Permissions.HubHealthRead, Permissions.MeTokens, Permissions.MeSessions,
        ],
        [Roles.IntegrationAdmin] =
        [
            Permissions.EpisodeRead, Permissions.HistoryRead, Permissions.EpisodeAck, Permissions.EpisodeAssign, Permissions.EpisodeNote, Permissions.EpisodeClose,
            Permissions.EpisodeRestore, Permissions.EpisodeSilence, Permissions.EpisodeBulk, Permissions.EpisodeRawPayloadRead, Permissions.HeartbeatManage,
            Permissions.IntegrationRead, Permissions.IntegrationManage, Permissions.MappingManage, Permissions.ReplayPreview, Permissions.ReplayRetry,
            Permissions.PolicyManage, Permissions.TeamManage, Permissions.DestinationManage, Permissions.SuppressionCreate, Permissions.SuppressionCreateLong,
            Permissions.AuditRead, Permissions.HubHealthRead, Permissions.MeTokens, Permissions.MeSessions,
        ],
        [Roles.PlatformAdmin] = new HashSet<string>(Permissions.All, StringComparer.Ordinal),
    };

    public static bool Has(IEnumerable<string> roles, string permission)
        => roles.Any(r => Matrix.TryGetValue(r, out var set) && set.Contains(permission));

    public static IReadOnlySet<string> PermissionsFor(IEnumerable<string> roles)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in roles)
        {
            if (Matrix.TryGetValue(role, out var set)) result.UnionWith(set);
        }
        return result;
    }
}
