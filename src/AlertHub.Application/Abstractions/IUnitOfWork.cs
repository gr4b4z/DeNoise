namespace AlertHub.Application.Abstractions;

/// <summary>Commits everything staged by repositories and the audit writer in one database transaction.</summary>
public interface IUnitOfWork
{
    Task CommitAsync(CancellationToken ct = default);
}
