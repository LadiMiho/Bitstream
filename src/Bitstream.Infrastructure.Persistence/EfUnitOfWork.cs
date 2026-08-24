using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Infrastructure.Persistence.Identity;
using Microsoft.EntityFrameworkCore.Storage;

namespace Bitstream.Infrastructure.Persistence;

/// <summary>
/// <see cref="IUnitOfWork"/> over <see cref="BitstreamDbContext"/> and
/// <see cref="BitstreamIdentityDbContext"/>. Repositories track changes on whichever context owns
/// the entity they touch — most on <see cref="BitstreamDbContext"/>, but anything loaded via
/// <c>UserManager&lt;User&gt;</c>/<c>RoleManager&lt;Role&gt;</c> (e.g. <c>IdentityService</c>
/// mutating <c>User.FailedLoginCount</c>/<c>Status</c> after <c>UserManager.FindByEmailAsync</c>)
/// is tracked by <see cref="BitstreamIdentityDbContext"/> instead. This is the one place that
/// calls <c>SaveChangesAsync</c>, so an application service controls exactly when a set of
/// mutations across either context becomes durable, without needing to know which one it is.
/// </summary>
public sealed class EfUnitOfWork : IUnitOfWork
{
    private readonly BitstreamDbContext _dbContext;
    private readonly BitstreamIdentityDbContext _identityDbContext;

    public EfUnitOfWork(BitstreamDbContext dbContext, BitstreamIdentityDbContext identityDbContext)
    {
        _dbContext = dbContext;
        _identityDbContext = identityDbContext;
    }

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var identityChanges = await _identityDbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var changes = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return identityChanges + changes;
    }

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
