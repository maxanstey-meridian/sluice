using System.Data.Common;
using System.Transactions;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Sluice.EntityFrameworkCore;

/// <summary>
/// The transaction a context's commands run in, read from the inputs EF's batch executor checks before it saves:
/// the context's own transaction, an enlisted one, or an ambient one. Never <c>Transaction.Current</c> directly: with
/// <c>Enlist=false</c> EF autocommits inside a scope.
/// </summary>
/// <param name="Ef">The underlying transaction of <c>Database.CurrentTransaction</c>, if any.</param>
/// <param name="Ambient">The enlisted or ambient transaction, if any.</param>
internal readonly record struct ContextTransaction(DbTransaction? Ef, Transaction? Ambient)
{
    internal bool IsNone => Ef is null && Ambient is null;

    internal static ContextTransaction Of(IInfrastructure<IServiceProvider> context)
    {
        var connection = context.GetService<IRelationalConnection>();
        var enlistment = connection as ITransactionEnlistmentManager;
        return new(
            connection.CurrentTransaction?.GetDbTransaction(),
            enlistment?.EnlistedTransaction ?? enlistment?.CurrentAmbientTransaction
        );
    }
}
