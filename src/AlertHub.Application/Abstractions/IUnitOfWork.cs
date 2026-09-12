namespace AlertHub.Application.Abstractions;

public interface IUnitOfWork
{
    Task CommitAsync(CancellationToken ct = default);

    /// <summary>Runs <paramref name="work"/> (which may commit several times) inside one database transaction; nested calls join the outer transaction.</summary>
    Task InTransactionAsync(Func<Task> work, CancellationToken ct = default);
}
