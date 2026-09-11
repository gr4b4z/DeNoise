namespace AlertHub.Domain.Episodes;

/// <summary>Timeline record (<c>alert.episode_event</c>). Append-only.</summary>
public sealed class EpisodeEvent
{
    public Guid Id { get; init; }
    public Guid EpisodeId { get; init; }
    public DateTimeOffset At { get; init; }
    public required string Kind { get; init; }
    public Guid? ActorId { get; init; }
    public Guid? EventId { get; init; }
    /// <summary>JSON detail (e.g. why an event was late, previous/new severity).</summary>
    public string? Detail { get; init; }
}
