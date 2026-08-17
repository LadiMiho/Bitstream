using Bitstream.Application.Abstractions.Persistence;
using Microsoft.EntityFrameworkCore.Storage;

namespace Bitstream.Infrastructure.Persistence;

/// <summary>
/// <see cref="IUnitOfWork"/> over <see cref="BitstreamDbContext"/>. Repositories track changes
/// on the context; this is the one place that calls <c>SaveChangesAsync</c>, so an application
/// service controls exactly when a set of mutations becomes durable.
/// </summary>
public sealed class EfUnitOfWork : IUnitOfWork
{
    private readonly BitstreamDbContext _dbContext;

    public EfUnitOfWork(BitstreamDbContext dbContext) => _dbContext = dbContext;

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _dbContext.SaveChangesAsync(cancellationToken);

    public async Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        return new TransactionScope(transaction);
    }

    public Task CommitAsync(CancellationToken cancellationToken = default) =>
        _dbContext.Database.CurrentTransaction?.CommitAsync(cancellationToken) ?? Task.CompletedTask;

    public Task RollbackAsync(CancellationToken cancellationToken = default) =>
        _dbContext.Database.CurrentTransaction?.RollbackAsync(cancellationToken) ?? Task.CompletedTask;

    /// <summary>
    /// Disposing without a prior <see cref="CommitAsync"/> rolls back — EF's own
    /// <see cref="IDbContextTransaction"/> behaviour — so a caller that throws between opening
    /// the transaction and committing it never leaves a partial write in place.
    /// </summary>
    private sealed class TransactionScope : IAsyncDisposable
    {
        private readonly IDbContextTransaction _transaction;

        public TransactionScope(IDbContextTransaction transaction) => _transaction = transaction;

        public ValueTask DisposeAsync() => _transaction.DisposeAsync();
    }
}
