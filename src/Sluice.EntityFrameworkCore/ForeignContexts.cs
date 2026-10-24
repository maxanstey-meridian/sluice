using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Sluice.Infrastructure;

namespace Sluice.EntityFrameworkCore;

/// <summary>
/// Refuses, inside a compute, an EF command on a context without <c>UseSluice</c>, which <see cref="TrackedReads"/>
/// never sees, or with no context: a write anywhere, a read outside a fused fetch (SLUICE-EF-015), and a read in one
/// inside a transaction (SLUICE-EF-002). It listens to EF's diagnostic events, which EF raises for every context,
/// interceptors or not. It also hands EF's transaction-disposed event, which no interceptor sees, to
/// <see cref="SluiceInterceptor.TransactionDisposed"/> (SLUICE-EF-007).
/// </summary>
/// <remarks>
/// One subscription for the process, to every EF <see cref="DiagnosticListener"/> (one per internal service provider),
/// those made before it included. It asks for EF's command-initialised event whether or not a compute is running: a
/// context that finds nobody listening stops raising its command events for a second, so a command right after one
/// outside a compute would go unseen. Listening to that event alone keeps them coming, since EF clears the pause when
/// anyone does. Throwing from it reaches the caller before the connection opens. Outside a compute each command costs
/// EF one event object and this one lookup, and each transaction's dispose one more.
/// </remarks>
internal sealed class ForeignContexts
    : IObserver<DiagnosticListener>,
        IObserver<KeyValuePair<string, object?>>
{
    private static readonly string Initialized = RelationalEventId.CommandInitialized.Name!;
    private static readonly string TransactionDisposed = RelationalEventId
        .TransactionDisposed
        .Name!;
    private static readonly ForeignContexts _instance = new();
    private static int _subscribed;

    private ForeignContexts() { }

    /// <summary>Subscribes, once per process.</summary>
    internal static void Subscribe()
    {
        if (Interlocked.Exchange(ref _subscribed, 1) == 0)
        {
            DiagnosticListener.AllListeners.Subscribe(_instance);
        }
    }

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name == DbLoggerCategory.Name)
        {
            listener.Subscribe(
                this,
                static name => name == Initialized || name == TransactionDisposed
            );
        }
    }

    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (value.Key == TransactionDisposed)
        {
            if (value.Value is TransactionEventData { Transaction: { } disposed })
            {
                SluiceInterceptor.TransactionDisposed(disposed);
            }

            return;
        }

        if (
            value.Key == Initialized
            && ActiveCompute.Reads is { } reads
            && value.Value is CommandEndEventData data
            && Refusal(reads, data) is { } refusal
        )
        {
            throw ActiveCompute.Violate(reads, refusal);
        }
    }

    // A command on a UseSluice context is TrackedReads' to check. One with no context may come from such a context,
    // but nothing says which, so it's checked here, as TrackedReads checks one: outside a fused fetch it isn't a query
    // Sluice analysed, and in one, the command's own transaction is the only one there is to read.
    private static InvalidOperationException? Refusal(SluiceReads reads, CommandEndEventData data)
    {
        var context = data.Context;
        if (context is not null && TrackedReads.IsOn(context.GetService<IDbContextOptions>()))
        {
            return null;
        }

        if (TrackedReads.IsWrite(data.CommandSource))
        {
            return TrackedReads.WriteInCompute(data.Command);
        }

        if (!ActiveCompute.InFetch(reads))
        {
            return context is null
                ? TrackedReads.NotALinqQuery(data.Command)
                : WithoutSluice(context, data.Command);
        }

        var inTransaction = context is null
            ? data.Command.Transaction is not null
            : !ContextTransaction.Of(context).IsNone;
        return inTransaction ? TrackedReads.InTransaction() : null;
    }

    private static InvalidOperationException WithoutSluice(DbContext context, DbCommand command)
    {
        var name = context.GetType().Name;
        return new(
            $"An EF command ({TrackedReads.Statement(command)}) ran inside a compute on a {name}, which isn't "
                + "configured with UseSluice, so Sluice can't record what it read and the cached value would go "
                + $"stale. Add .UseSluice(services) to {name}'s options and query it through read.Db<{name}>(), or "
                + "run it in the fetch of read.From(dependency, fetch) with a Source you declare."
        );
    }

    public void OnCompleted() { }

    public void OnError(Exception error) { }
}
