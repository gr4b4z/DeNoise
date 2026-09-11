using AlertHub.Application.Abstractions;

namespace AlertHub.Infrastructure.Persistence;

/// <summary>One <c>SaveChanges</c> = one transaction: every staged row commits or none does.</summary>
public sealed class EfUnitOfWork(AlertHubDbContext db) : IUnitOfWork
{
    public Task CommitAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
