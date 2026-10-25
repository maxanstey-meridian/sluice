using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Sluice.Infrastructure;

namespace Sluice.EntityFrameworkCore;

/// <summary>
/// Refuses, inside a compute, an EF command on a context without <c>UseSluice</c>, which <see cref="TrackedReads"/>
/// never sees: a write anywhere, a read outside a fused fetch (SLUICE-EF-015), and a read in one inside a transaction
/// (SLUICE-EF-002); and every command with no context, which may come from either. It listens to EF's diagnostic
/// events, which EF raises for every context, interceptors or not. It also hands EF's transaction events to
/// <see cref="SluiceInterceptor.TransactionEnded"/> and <see cref="SluiceInterceptor.TransactionErred"/>
/// (SLUICE-EF-007): a commit, rollback or failure on a context without <c>UseSluice</c>, which may end a transaction a
/// <c>UseSluice</c> context shares through <c>UseTransaction</c>, and the disposal of a transaction EF began, which
/// no interceptor sees.
/// </summary>
/// <remarks>
/// One subscription for the process, to every EF <see cref="DiagnosticListener"/> (one per internal service provider),
/// those made before it included. It asks for EF's command-initialised event whether or not a compute is running: a
/// context that finds nobody listening stops raising its command events for a second, so a command right after one
/// outside a compute would go unseen. Listening to that event alone keeps them coming, since EF clears the pause when
/// anyone does. Throwing from it reaches the caller before the connection opens. Outside a compute each command costs
/// EF one event object and this one lookup, and each transaction's commit, rollback, failure and dispose one more.
/// </remarks>
internal sealed class ForeignContexts
    : IObserver<DiagnosticListener>,
        IObserver<KeyValuePair<string, object?>>
{
    private static readonly string Initialized = RelationalEventId.CommandInitialized.Name!;
    private static readonly string Committed = RelationalEventId.TransactionCommitted.Name!;
    private static readonly string RolledBack = RelationalEventId.TransactionRolledBack.Name!;
    private static readonly string Erred = RelationalEventId.TransactionError.Name!;
    private static readonly string Disposed = RelationalEventId.TransactionDisposed.Name!;
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
                static name =>
                    name == Initialized
                    || name == Committed
                    || name == RolledBack
                    || name == Erred
                    || name == Disposed
            );
        }
    }

    public void OnNext(KeyValuePair<string, object?> value)
    {
        switch (value)
        {
            // EF raises the disposal only for a transaction it began, and no interceptor sees it.
            case { Key: var key, Value: TransactionEventData { Transaction: { } disposed } }
                when key == Disposed:
                SluiceInterceptor.TransactionEnded(disposed, seenBy: null);
                return;
            case { Key: var key, Value: TransactionEndEventData { Transaction: { } ended } end }
                when key == Committed || key == RolledBack:
                SluiceInterceptor.TransactionEnded(ended, seenBy: end.Context);
                return;
            case { Key: var key, Value: TransactionErrorEventData { Transaction: { } erred } error }
                when key == Erred:
                SluiceInterceptor.TransactionErred(erred, seenBy: error.Context);
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
    // and says neither which nor what transaction it runs in, so it's refused here, a fused fetch included: EF runs
    // one only for a provider's own work, never for a query (Npgsql's HiLo key sequence, when an entity is added or
    // attached).
    private static InvalidOperationException? Refusal(SluiceReads reads, CommandEndEventData data)
    {
        if (data.Context is not { } context)
        {
            return NoContext(data.Command);
        }

        if (TrackedReads.IsOn(context.GetService<IDbContextOptions>()))
        {
            return null;
        }

        if (TrackedReads.IsWrite(data.CommandSource))
        {
            return TrackedReads.WriteInCompute(data.Command);
        }

        if (!ActiveCompute.InFetch(reads))
        {
            return WithoutSluice(context, data.Command);
        }

        return ContextTransaction.Of(context).IsNone ? null : TrackedReads.InTransaction();
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

    private static InvalidOperationException NoContext(DbCommand command) =>
        new(
            $"An EF command with no context ran inside a compute ({TrackedReads.Statement(command)}), so Sluice "
                + "can't tell what it read, or which transaction it ran in. EF runs one for a provider's own work, "
                + "such as taking a key from a HiLo sequence when an entity is added or attached, which writes: "
                + "add or attach entities before or after GetOrSetAsync. A command the app builds and runs itself "
                + "without a context has none either: run it before or after GetOrSetAsync, or give it the context it "
                + "belongs to."
        );

    public void OnCompleted() { }

    public void OnError(Exception error) { }
}
