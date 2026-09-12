namespace DeNoise.Application.Ops;

public sealed class JobQueueOptions
{
    public const string Section = "JobQueue";
    /// <summary>How long a claim is owned before the reaper may release it.</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>Jobs claimed per poll.</summary>
    public int BatchSize { get; set; } = 20;
    /// <summary>Poll interval while the queue is empty.</summary>
    public TimeSpan IdlePoll { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>Reaper cadence (05 §7: 30 s).</summary>
    public TimeSpan ReapInterval { get; set; } = TimeSpan.FromSeconds(30);
}
