using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Sluice.EntityFrameworkCore.Tests.Support;

/// <summary>
/// Once armed, fails the next command, or the next commit, with an error a retrying execution strategy retries.
/// </summary>
public sealed class FailOnceTransiently(bool atCommit)
    : DbCommandInterceptor,
        IDbTransactionInterceptor
{
    private bool _armed;

    public void Arm() => _armed = true;

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default
    )
    {
        FailIfArmed(!atCommit);
        return new(result);
    }

    public ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default
    )
    {
        FailIfArmed(atCommit);
        return new(result);
    }

    private void FailIfArmed(bool here)
    {
        if (here && _armed)
        {
            _armed = false;
            throw new TimeoutException("A transient failure.");
        }
    }
}
