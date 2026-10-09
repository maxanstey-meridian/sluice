using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Sluice.EntityFrameworkCore.Tests.Support;

/// <summary>
/// Saves its context once more from inside a save, sync or async like the save, as an app interceptor that writes an
/// audit row would: from <c>SavingChanges</c> when <paramref name="whileSaving"/>, otherwise from
/// <c>SavedChanges</c>. <see cref="Once"/> sets what the second save changes.
/// </summary>
public sealed class SaveAgain(bool whileSaving) : SaveChangesInterceptor
{
    private Action? _change;
    private bool _inAbandonedTransaction;

    /// <summary>
    /// Makes the next save save again with <paramref name="change"/>; inside a transaction it disposes without
    /// committing when <paramref name="inAbandonedTransaction"/>, as a <c>using</c> block does when the code after the
    /// save throws.
    /// </summary>
    public void Once(Action change, bool inAbandonedTransaction = false)
    {
        _change = change;
        _inAbandonedTransaction = inAbandonedTransaction;
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result
    )
    {
        if (whileSaving)
        {
            Save(eventData.Context!);
        }

        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    )
    {
        if (whileSaving)
        {
            await SaveAsync(eventData.Context!, cancellationToken);
        }

        return result;
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (!whileSaving)
        {
            Save(eventData.Context!);
        }

        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default
    )
    {
        if (!whileSaving)
        {
            await SaveAsync(eventData.Context!, cancellationToken);
        }

        return result;
    }

    private void Save(DbContext context)
    {
        if (TakeChange() is not { } change)
        {
            return;
        }

        using var transaction = _inAbandonedTransaction
            ? context.Database.BeginTransaction()
            : null;
        change();
        context.SaveChanges();
    }

    private async Task SaveAsync(DbContext context, CancellationToken cancellationToken)
    {
        if (TakeChange() is not { } change)
        {
            return;
        }

        await using var transaction = _inAbandonedTransaction
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;
        change();
        await context.SaveChangesAsync(cancellationToken);
    }

    private Action? TakeChange()
    {
        var change = _change;
        _change = null;
        return change;
    }
}
