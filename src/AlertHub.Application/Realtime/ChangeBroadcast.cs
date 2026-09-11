using System.Text.Json;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Processing;
using AlertHub.Domain.Common;
using AlertHub.Domain.Episodes;

namespace AlertHub.Application.Realtime;

/// <summary>A change notification for the UI stream (ADR-12). <c>Scope</c> is the access scope so subscribers only see what they may see.</summary>
public sealed record ChangeEvent(string Type, string Scope, string DataJson)
{
    public const string EpisodeChanged = "episode.changed";
    public const string CoverageChanged = "coverage.changed";
    public const string HeartbeatChanged = "heartbeat.changed";
    public const string HubHealth = "hub.health";

    public static ChangeEvent ForEpisode(Episode episode) => new(EpisodeChanged, episode.AccessScope, JsonSerializer.Serialize(new
    {
        id = episode.EpisodeId,
        version = episode.Version,
        handling = episode.HandlingState,
        condition = episode.ConditionState,
        severity = episode.Severity.ToWire(),
        teamId = episode.OwningTeamId,
        closed = !episode.IsOpen,
    }, JsonDefaults.Stored));
}

/// <summary>Cross-process fan-out (PostgreSQL NOTIFY in production). Best effort; the UI resyncs on gaps.</summary>
public interface IChangeBroadcaster
{
    Task PublishAsync(ChangeEvent change, CancellationToken ct = default);
}

/// <summary>Bridges processing commits to the broadcaster.</summary>
public sealed class BroadcastingEpisodeChangePublisher(IChangeBroadcaster broadcaster) : IEpisodeChangePublisher
{
    public Task PublishAsync(Episode episode, CancellationToken ct) => broadcaster.PublishAsync(ChangeEvent.ForEpisode(episode), ct);
}
