namespace AlertHub.Workers;

/// <summary>Roles hosted by the workers image, selected with <c>--roles=processing,scheduler,dispatcher</c> or <c>Workers:Roles</c> (03 §1).</summary>
public sealed record WorkerRoles(bool Processing, bool Scheduler, bool Dispatcher)
{
    public const string Section = "Workers";

    public static WorkerRoles Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new WorkerRoles(true, true, true);
        var parts = value.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant()).ToHashSet();
        var unknown = parts.Except(["processing", "scheduler", "dispatcher"]).ToList();
        if (unknown.Count > 0) throw new ArgumentException($"Unknown worker role(s): {string.Join(", ", unknown)}");
        return new WorkerRoles(parts.Contains("processing"), parts.Contains("scheduler"), parts.Contains("dispatcher"));
    }

    public override string ToString() => string.Join(",", new[] { Processing ? "processing" : null, Scheduler ? "scheduler" : null, Dispatcher ? "dispatcher" : null }.Where(s => s is not null));
}
