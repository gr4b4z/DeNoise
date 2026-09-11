using AlertHub.Domain.Ops;

namespace AlertHub.Application.Ops;

/// <summary>Executes one job kind. Handlers must be idempotent: a job whose worker died after committing state is re-run.</summary>
public interface IJobHandler
{
    string Kind { get; }
    Task HandleAsync(Job job, JobContext context, CancellationToken ct);
}

/// <summary>Per-execution services a handler may use, including lease extension for long jobs.</summary>
public sealed class JobContext(string workerId, Func<TimeSpan, CancellationToken, Task<bool>> extendLease)
{
    public string WorkerId { get; } = workerId;
    public Task<bool> ExtendLeaseAsync(TimeSpan lease, CancellationToken ct) => extendLease(lease, ct);
}
