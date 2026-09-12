using DeNoise.Domain.Audit;

namespace DeNoise.Application.Abstractions;

/// <summary>Who is performing an operation, for audit and authorisation. Correlation id ties audit rows to a request or job.</summary>
public sealed record Actor(string Type, string Id, string? Display, string CorrelationId, System.Net.IPAddress? Ip = null)
{
    public static Actor System(string correlationId, string id = "system") => new(ActorTypes.System, id, "DeNoise", correlationId);
    public static Actor Integration(Guid integrationId, string correlationId, System.Net.IPAddress? ip = null)
        => new(ActorTypes.Integration, integrationId.ToString(), null, correlationId, ip);
}
