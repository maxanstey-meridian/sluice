using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Sluice.EntityFrameworkCore.Tests.Support;

/// <summary>
/// Fails every release of a savepoint, before it reaches the database. EF reports the failure as a failed
/// transaction, then carries on: the transaction stays usable.
/// </summary>
public sealed class FailReleaseSavepoint : DbTransactionInterceptor
{
    public override InterceptionResult ReleasingSavepoint(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result
    ) => throw new InvalidOperationException("Releasing the savepoint failed.");

    public override ValueTask<InterceptionResult> ReleasingSavepointAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default
    ) => throw new InvalidOperationException("Releasing the savepoint failed.");
}
