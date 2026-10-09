using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Sluice.EntityFrameworkCore.Tests.Support;

/// <summary>
/// Cancels <see cref="Request"/> as soon as EF reports a save completed or a transaction committed, before the
/// interceptors registered after it run: a request aborted just after its write committed.
/// </summary>
public sealed class CancelAfterCommit : ISaveChangesInterceptor, IDbTransactionInterceptor
{
    public CancellationTokenSource? Request { get; set; }

    public async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default
    )
    {
        await Cancel();
        return result;
    }

    public Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default
    ) => Cancel();

    private Task Cancel() => Request?.CancelAsync() ?? Task.CompletedTask;
}
