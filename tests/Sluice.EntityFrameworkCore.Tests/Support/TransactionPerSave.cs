using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace Sluice.EntityFrameworkCore.Tests.Support;

/// <summary>
/// Wraps each async save in a transaction of its own: begins it in <c>SavingChanges</c> and commits it in
/// <c>SavedChanges</c>, as an app interceptor that adds an outbox row in the same transaction might. Added before
/// Sluice, it commits before Sluice sees the save complete. Sync saves pass through.
/// </summary>
public sealed class TransactionPerSave : SaveChangesInterceptor
{
    private IDbContextTransaction? _transaction;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    )
    {
        _transaction = await eventData.Context!.Database.BeginTransactionAsync(cancellationToken);
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default
    )
    {
        await using var transaction = _transaction!;
        _transaction = null;
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
