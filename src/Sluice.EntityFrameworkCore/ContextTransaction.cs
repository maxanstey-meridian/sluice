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
/// <param name="EfId">
/// EF's id for <c>Database.CurrentTransaction</c>, if any: the provider may hand the same <see cref="DbTransaction"/>
/// out again for the next transaction (Npgsql does, per connection), but EF gives each its own id.
/// </param>
/// <param name="Ambient">The enlisted or ambient transaction, if any.</param>
internal readonly record struct ContextTransaction(
    DbTransaction? Ef,
    Guid? EfId,
    Transaction? Ambient
)
{
    internal const string NotRelational =
        "Sluice supports relational EF providers only, and this context's provider isn't relational.";

    internal bool IsNone => Ef is null && Ambient is null;

    /// <exception cref="NotSupportedException">The context's provider isn't relational.</exception>
    internal static ContextTransaction Of(IInfrastructure<IServiceProvider> context)
    {
        var connection = Connection(context);
        var enlistment = connection as ITransactionEnlistmentManager;
        return new(
            connection.CurrentTransaction?.GetDbTransaction(),
            connection.CurrentTransaction?.TransactionId,
            enlistment?.EnlistedTransaction ?? enlistment?.CurrentAmbientTransaction
        );
    }

    /// <summary>The context's relational connection, which carries its transaction and runs its SQL commands.</summary>
    /// <exception cref="NotSupportedException">The context's provider isn't relational.</exception>
    internal static IRelationalConnection Connection(IInfrastructure<IServiceProvider> context) =>
        context.Instance.GetService(typeof(IRelationalConnection)) as IRelationalConnection
        ?? throw new NotSupportedException(NotRelational);

    /// <summary>
    /// Whether <paramref name="transaction"/> has ended outside EF: committed, rolled back or disposed on the
    /// <see cref="DbTransaction"/> itself, which raises no EF event, so EF's <c>CurrentTransaction</c> still holds it.
    /// A provider detaches an ended transaction from its connection, or refuses to read its isolation level (Npgsql
    /// keeps the connection, and throws on <c>IsolationLevel</c>, but not for a transaction a failed statement aborted,
    /// which is still open).
    /// </summary>
    internal static bool Ended(DbTransaction transaction)
    {
        try
        {
            if (transaction.Connection is null)
            {
                return true;
            }

            _ = transaction.IsolationLevel;
            return false;
        }
        catch (InvalidOperationException)
        {
            // ObjectDisposedException included: the provider disposed it.
            return true;
        }
    }

    /// <summary>
    /// Whether <paramref name="transaction"/> runs on <paramref name="connection"/>, as the provider has it now. A
    /// provider may hand the <see cref="DbTransaction"/> object to another connection: Npgsql's belongs to the physical
    /// connection, which its pool hands to whichever <see cref="DbConnection"/> opens next. So a context whose
    /// connection the app closed and opened again itself, which EF raises no event for, may still hold the object
    /// another connection's transaction runs on, and the context's commands run outside it.
    /// </summary>
    internal static bool On(DbTransaction transaction, DbConnection connection)
    {
        try
        {
            return ReferenceEquals(transaction.Connection, connection);
        }
        catch (InvalidOperationException)
        {
            // ObjectDisposedException included: the provider disposed it.
            return false;
        }
    }

    /// <summary>Whether <paramref name="transaction"/> can still commit: it hasn't completed or been disposed.</summary>
    internal static bool Active(Transaction transaction)
    {
        try
        {
            return transaction.TransactionInformation.Status is TransactionStatus.Active;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}
