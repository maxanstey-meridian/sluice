using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Sluice.EntityFrameworkCore.Tests.Support;

/// <summary>
/// Fails every rollback to a savepoint, before it reaches the database. EF reports the failure as a failed
/// transaction and throws it to the caller; the writes since the savepoint stay in the transaction.
/// </summary>
public sealed class FailRollbackToSavepoint : DbTransactionInterceptor
{
    public override InterceptionResult RollingBackToSavepoint(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result
    ) => throw new InvalidOperationException("Rolling back to the savepoint failed.");

    public override ValueTask<InterceptionResult> RollingBackToSavepointAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default
    ) => throw new InvalidOperationException("Rolling back to the savepoint failed.");
}
