using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Sluice.EntityFrameworkCore.Tests.Support;

/// <summary>Begins no transaction when EF begins one, and hands EF <paramref name="transaction"/> instead.</summary>
public sealed class BeginInstead(DbTransaction transaction) : DbTransactionInterceptor
{
    public override InterceptionResult<DbTransaction> TransactionStarting(
        DbConnection connection,
        TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result
    ) => InterceptionResult<DbTransaction>.SuppressWithResult(transaction);

    public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
        DbConnection connection,
        TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result,
        CancellationToken cancellationToken = default
    ) => ValueTask.FromResult(InterceptionResult<DbTransaction>.SuppressWithResult(transaction));
}
