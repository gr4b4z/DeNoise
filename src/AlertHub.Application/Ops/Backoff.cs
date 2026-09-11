namespace AlertHub.Application.Ops;

/// <summary>Exponential backoff with full jitter (ADR-2): 5 s · 2^(attempt−1), capped at one hour.</summary>
public static class Backoff
{
    public static readonly TimeSpan Base = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Cap = TimeSpan.FromHours(1);

    public static TimeSpan For(int attempt, Random? random = null)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 20);
        var max = Math.Min(Cap.TotalSeconds, Base.TotalSeconds * Math.Pow(2, exponent));
        var jittered = (random ?? Random.Shared).NextDouble() * max;
        return TimeSpan.FromSeconds(Math.Max(1, jittered));
    }
}
