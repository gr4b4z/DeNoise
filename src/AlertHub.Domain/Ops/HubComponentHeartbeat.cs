namespace AlertHub.Domain.Ops;

/// <summary>Liveness record of one Hub component instance (<c>ops.hub_component_heartbeat</c>), surfaced on the hub health screen.</summary>
public sealed class HubComponentHeartbeat
{
    public required string Component { get; init; }
    public required string Instance { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}
