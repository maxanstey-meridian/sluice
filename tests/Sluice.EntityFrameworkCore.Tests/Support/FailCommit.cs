using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Sluice.EntityFrameworkCore.Tests.Support;

/// <summary>Makes every commit fail, as a connection dropped mid-commit would, leaving its outcome unknown.</summary>
public sealed class FailCommit : DbTransactionInterceptor
{
    public override InterceptionResult TransactionCommitting(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result
    ) => throw new InvalidOperationException("The commit failed.");

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default
    ) => throw new InvalidOperationException("The commit failed.");
}
