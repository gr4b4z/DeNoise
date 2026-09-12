using AlertHub.Application.Abstractions;

namespace AlertHub.Infrastructure.Persistence;

public sealed class EfUnitOfWork(AlertHubDbContext db) : IUnitOfWork
{
    public Task CommitAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);

    public async Task InTransactionAsync(Func<Task> work, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            await work();
            return;
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await work();
        await tx.CommitAsync(ct);
    }
}
