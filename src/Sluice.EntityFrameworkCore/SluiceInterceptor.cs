using System.Collections.Concurrent;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sluice.Infrastructure;

namespace Sluice.EntityFrameworkCore;

/// <summary>
/// Invalidates what each tracked save, <c>ExecuteUpdate</c> and <c>ExecuteDelete</c> wrote, once it has committed.
/// One instance per <see cref="SluiceCache"/>, shared by every context that uses it, so contexts sharing one
/// transaction share its pending invalidations.
/// </summary>
/// <remarks>
/// Never drops: every outcome that might have committed something publishes, because over-invalidating is safe.
/// Publishing after a commit logs failures instead of throwing, since a throw would make the caller retry a write
/// that already committed. Sync EF calls publish synchronously.
/// </remarks>
internal sealed partial class SluiceInterceptor
    : ISaveChangesInterceptor,
        IDbTransactionInterceptor,
        IDbCommandInterceptor,
        IDbConnectionInterceptor
{
    private static readonly ConditionalWeakTable<SluiceCache, SluiceInterceptor> _instances = new();

    private readonly SluiceCache _cache;
    private readonly ILogger _logger;

    // A save in progress, from SavingChanges until EF reports how it ended, with what it writes once its first command
    // has been initialised. Weak, so a context that's dropped mid-save leaves nothing behind.
    private readonly ConditionalWeakTable<DbContext, SaveInProgress> _saves = new();

    // What a bulk write (ExecuteUpdate, ExecuteDelete) took when EF initialised its command, held against the command
    // until it ends: the keys a keyed one (ForKey, ForKeys) pinned, and the ambient transaction it ran in. Weak, so a
    // command EF drops leaves nothing behind.
    private readonly ConditionalWeakTable<DbCommand, BulkCommand> _bulk = new();

    // What saves and bulk writes inside an EF transaction wrote, published when the transaction ends.
    private readonly EfPending _efPending = new();

    // What saves inside an enlisted or ambient transaction wrote, until it completes: each publishes from its own
    // TransactionCompleted handler, so this is only for the uncommitted-write guard to read (SLUICE-EF-025).
    private readonly ConditionalWeakTable<Transaction, PendingWrites> _ambientPending = new();

    // The ambient transaction each connection was opened in, which Npgsql enlists it in, until it closes. An async write
    // inside a TransactionScope without TransactionScopeAsyncFlowOption.Enabled can't see the scope where it completes,
    // and on a cold connection pool not even at its first command. Weak, so a dropped connection leaves nothing behind.
    private readonly ConditionalWeakTable<DbConnection, Transaction> _openedIn = new();

    // How the core reads an ambient transaction's pending writes, made once so registering it again is the same value.
    private readonly Func<Transaction, IReadOnlyCollection<Dependency>> _ambientWrites;

    // Each entity type that has logged invalidating its foreign keys and unique columns whole, so each logs once per
    // cache.
    private readonly ConcurrentDictionary<string, bool> _loggedUnversioned = new();

    // Whether a write has logged that its TransactionScope didn't flow to where it completed, so it logs once per cache.
    private int _loggedHiddenScope;

    private SluiceInterceptor(SluiceCache cache, ILogger<SluiceInterceptor> logger)
    {
        _cache = cache;
        _logger = logger;
        _ambientWrites = transaction =>
            _ambientPending.TryGetValue(transaction, out var pending) ? pending.Pending() : [];
    }

    /// <summary>The interceptor for the <see cref="SluiceCache"/> registered in <paramref name="services"/>.</summary>
    /// <exception cref="InvalidOperationException">
    /// Sluice, or its EF Core package, isn't registered in <paramref name="services"/>.
    /// </exception>
    internal static SluiceInterceptor For(IServiceProvider services)
    {
        var cache =
            services.GetService<SluiceCache>()
            ?? throw new InvalidOperationException(
                "UseSluice needs Sluice registered in the application's services: call services.AddSluice() "
                    + "where you register your services."
            );
        _ =
            services.GetService<SluiceEntityFrameworkCoreServiceCollectionExtensions.Registration>()
            ?? throw new InvalidOperationException(
                "UseSluice needs Sluice's EF Core package registered in the application's services: call "
                    + "services.AddSluiceEntityFrameworkCore() where you call AddSluice, so Sluice checks from the "
                    + "first compute that it queries no context without UseSluice."
            );
        var interceptor = _instances.GetValue(
            cache,
            cache => new(cache, services.GetRequiredService<ILogger<SluiceInterceptor>>())
        );

        // From the instance the table kept, not its constructor: GetValue can construct one per racing caller and keep
        // only one, and the cache holds one lookup, the last registered. Every call registers the kept one's.
        FlowWrites.OnAmbient(cache, interceptor._ambientWrites);
        StoreChecks.Add(cache, CachedValues.Check);
        return interceptor;
    }

    // What the save writes is read at its first command, after every SavingChanges interceptor and EF's own change
    // detection and cascades, so this only starts the save, and publishes what's left of earlier ones: one its context
    // left unfinished, and what its EF transaction held, if that was committed or rolled back outside EF. A provider
    // may refuse the save's commands in such a transaction (Npgsql does), so this may be the last Sluice sees of it
    // until its connection closes.
    public InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result
    )
    {
        if (eventData.Context is { } context)
        {
            var leftover = Start(context);
            Publish(() => Earlier(context, leftover));
        }

        return result;
    }

    public async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    )
    {
        if (eventData.Context is { } context)
        {
            var leftover = Start(context);
            await PublishAsync(() => Earlier(context, leftover)).ConfigureAwait(false);
        }

        return result;
    }

    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Complete(Save(eventData.Context, keep: false, rows: result));
        return result;
    }

    // The token is ignored throughout: a request cancelled after its write committed must still invalidate.
    public async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default
    )
    {
        await CompleteAsync(Save(eventData.Context, keep: false, rows: result))
            .ConfigureAwait(false);
        return result;
    }

    // A failed or cancelled save may have committed earlier batches (without a transaction, or under
    // AutoTransactionBehavior.Never), so it publishes too.
    public void SaveChangesFailed(DbContextErrorEventData eventData) =>
        Complete(Save(eventData.Context, keep: false));

    public Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default
    ) => CompleteAsync(Save(eventData.Context, keep: false));

    public void SaveChangesCanceled(DbContextEventData eventData) =>
        Complete(Save(eventData.Context, keep: false));

    public Task SaveChangesCanceledAsync(
        DbContextEventData eventData,
        CancellationToken cancellationToken = default
    ) => CompleteAsync(Save(eventData.Context, keep: false));

    // EF reports a concurrency failure here and not through SaveChangesFailed. The save is kept: if an interceptor
    // suppresses the exception, the save goes on and publishes again when it completes.
    public InterceptionResult ThrowingConcurrencyException(
        ConcurrencyExceptionEventData eventData,
        InterceptionResult result
    )
    {
        Complete(Save(eventData.Context, keep: true));
        return result;
    }

    public async ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
        ConcurrencyExceptionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default
    )
    {
        await CompleteAsync(Save(eventData.Context, keep: true)).ConfigureAwait(false);
        return result;
    }

    // A bulk write is one statement. Whether it failed or was cancelled before or after the database ran it, publishing
    // is safe; EF reports a cancellation through CommandCanceled, not CommandFailed. One that ran reports how many rows
    // it changed (ExecuteUpdate and ExecuteDelete return it): none changed nothing, and cascaded to nothing, since the
    // database cascades from each row it deletes, so it publishes nothing, and leaves nothing for a transaction. What
    // its command took is still taken off it, since the provider may hand it out again.
    public int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        var write = BulkWrite(command, eventData);
        if (result != 0)
        {
            Complete(write);
        }

        return result;
    }

    public async ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default
    )
    {
        var write = BulkWrite(command, eventData);
        if (result != 0)
        {
            await CompleteAsync(write).ConfigureAwait(false);
        }

        return result;
    }

    public void CommandFailed(DbCommand command, CommandErrorEventData eventData) =>
        Complete(BulkWrite(command, eventData));

    public Task CommandFailedAsync(
        DbCommand command,
        CommandErrorEventData eventData,
        CancellationToken cancellationToken = default
    ) => CompleteAsync(BulkWrite(command, eventData));

    public void CommandCanceled(DbCommand command, CommandEndEventData eventData) =>
        Complete(BulkWrite(command, eventData));

    public Task CommandCanceledAsync(
        DbCommand command,
        CommandEndEventData eventData,
        CancellationToken cancellationToken = default
    ) => CompleteAsync(BulkWrite(command, eventData));

    // The first command of a save captures it: every SavingChanges interceptor has run, and EF has detected changes and
    // applied its own cascades, but nothing is written yet, and EF accepts no change until every batch has run. A later
    // batch, and the execution strategy's retry, find it captured. A save with nothing to write runs no command, and
    // publishes nothing.
    //
    // The first bulk command EF initialises on the flow while a keyed call runs takes its pin: the statement the call
    // runs, initialised before any interceptor's NonQueryExecuting could start another, and before any command ends, so
    // the pin goes to it whatever order interceptors run in and whatever its row count. A later command on the flow
    // (another bulk write an interceptor runs, a retry by the execution strategy) finds the pin taken, and publishes
    // whole. A provider may hand out a command object again (Npgsql does, per open connection), so each bulk command
    // replaces what an earlier statement left on it.
    //
    // Both refuse a model whose converters Sluice can't trust here, before anything is written.
    public DbCommand CommandInitialized(CommandEndEventData eventData, DbCommand result)
    {
        if (eventData.Context is not { } context)
        {
            return result;
        }

        switch (eventData.CommandSource)
        {
            case CommandSource.SaveChanges:
                var save = _saves.GetValue(context, _ => new());
                save.Capture ??= Capture(context);
                break;
            case CommandSource.ExecuteUpdate or CommandSource.ExecuteDelete:
                ModelSources.EnsureRoundTrips(context.Model);
                _bulk.AddOrUpdate(result, new(KeyPin.Take(), CommandTransaction(context).Ambient));
                break;
        }

        return result;
    }

    // A provider may hand out the same DbTransaction object again (Npgsql does, per connection). Anything still
    // pending on it belongs to an earlier transaction that ended without a commit or rollback EF saw: one committed or
    // rolled back outside EF, or disposed without either, or one whose commit failed. It publishes now, since the
    // first may have committed it, and over-invalidating the others is safe.
    public DbTransaction TransactionStarted(
        DbConnection connection,
        TransactionEndEventData eventData,
        DbTransaction result
    )
    {
        Publish(() => _efPending.Take(result)?.All() ?? []);
        AddProbe(eventData);
        return result;
    }

    public async ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection,
        TransactionEndEventData eventData,
        DbTransaction result,
        CancellationToken cancellationToken = default
    )
    {
        await PublishAsync(() => _efPending.Take(result)?.All() ?? []).ConfigureAwait(false);
        AddProbe(eventData);
        return result;
    }

    // A context joining a transaction (UseTransaction) shares what the transaction has pending, so nothing is removed.
    public DbTransaction TransactionUsed(
        DbConnection connection,
        TransactionEventData eventData,
        DbTransaction result
    )
    {
        AddProbe(eventData);
        return result;
    }

    public ValueTask<DbTransaction> TransactionUsedAsync(
        DbConnection connection,
        TransactionEventData eventData,
        DbTransaction result,
        CancellationToken cancellationToken = default
    )
    {
        AddProbe(eventData);
        return new(result);
    }

    public void TransactionCommitted(
        DbTransaction transaction,
        TransactionEndEventData eventData
    ) => Publish(() => _efPending.Take(transaction)?.All() ?? []);

    public Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default
    ) => PublishAsync(() => _efPending.Take(transaction)?.All() ?? []);

    // A rollback publishes too: over-invalidating is safe.
    public void TransactionRolledBack(
        DbTransaction transaction,
        TransactionEndEventData eventData
    ) => Publish(() => _efPending.Take(transaction)?.All() ?? []);

    public Task TransactionRolledBackAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default
    ) => PublishAsync(() => _efPending.Take(transaction)?.All() ?? []);

    // A failed commit's outcome is unknown, so it publishes. EF also reports a savepoint that failed to release, or to
    // roll back to, here and carries on, and the transaction may still commit more after it, so the pending writes are
    // kept for its commit or rollback to publish again, and no savepoint changes.
    public void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) =>
        Publish(() => _efPending.Find(transaction)?.All() ?? []);

    public Task TransactionFailedAsync(
        DbTransaction transaction,
        TransactionErrorEventData eventData,
        CancellationToken cancellationToken = default
    ) => PublishAsync(() => _efPending.Find(transaction)?.All() ?? []);

    // Savepoints EF makes, its own around a save in a transaction (EF's __EFSavePoint) and the app's through EF. Rolling
    // back to one undoes what was written since, which then stops counting as pending (SLUICE-EF-026), but still
    // publishes when the transaction ends, as a rollback does. EF names none in its events, so Sluice keeps a stack:
    // a release pops the innermost, and a rollback pops nothing, as PostgreSQL keeps the savepoint it rolls back to.
    // The database drops at least as many as Sluice does, so Sluice's innermost is never older than the one the
    // database rolls back to, and Sluice can undo less than it did, never more.
    public void CreatedSavepoint(DbTransaction transaction, TransactionEventData eventData) =>
        CreateSavepoint(transaction, eventData);

    public Task CreatedSavepointAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        CancellationToken cancellationToken = default
    )
    {
        CreateSavepoint(transaction, eventData);
        return Task.CompletedTask;
    }

    public void RolledBackToSavepoint(DbTransaction transaction, TransactionEventData eventData) =>
        RollBackToSavepoint(transaction, eventData);

    public Task RolledBackToSavepointAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        CancellationToken cancellationToken = default
    )
    {
        RollBackToSavepoint(transaction, eventData);
        return Task.CompletedTask;
    }

    public void ReleasedSavepoint(DbTransaction transaction, TransactionEventData eventData) =>
        _efPending.Find(transaction)?.Release();

    public Task ReleasedSavepointAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        CancellationToken cancellationToken = default
    )
    {
        _efPending.Find(transaction)?.Release();
        return Task.CompletedTask;
    }

    // Notes the save the context has in progress, if any: EF makes its own savepoint before the save's first command,
    // so a rollback to it undoes everything that save writes, whenever the save gets to publish.
    private void CreateSavepoint(DbTransaction transaction, TransactionEventData eventData)
    {
        if (transaction.Connection is { } connection)
        {
            _efPending
                .For(transaction, connection)
                .Save(
                    eventData.Context is { } context && _saves.TryGetValue(context, out var save)
                        ? save
                        : null
                );
        }
    }

    private void RollBackToSavepoint(DbTransaction transaction, TransactionEventData eventData)
    {
        if (
            _efPending.Find(transaction)?.RollBack() is { } during
            && eventData.Context is { } context
            && _saves.TryGetValue(context, out var save)
            && ReferenceEquals(save, during)
        )
        {
            save.RolledBack = true;
        }
    }

    // EF opens a context's connection inside a TransactionScope on the scope's own thread, where Transaction.Current is
    // set even when the scope doesn't flow into async code, and Npgsql enlists the connection in it. With
    // Enlist=false, the context has no ambient transaction, and none is recorded.
    public InterceptionResult ConnectionOpening(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result
    )
    {
        RecordOpening(connection, eventData);
        return result;
    }

    public ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default
    )
    {
        RecordOpening(connection, eventData);
        return new(result);
    }

    // Closing a connection ends every transaction on it, so what any still has pending publishes: one committed or
    // rolled back outside EF and then disposed, which EF raises no event for. EF has disposed its transaction by now.
    public InterceptionResult ConnectionClosing(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result
    )
    {
        Publish(() => _efPending.TakeOn(connection));
        return result;
    }

    public async ValueTask<InterceptionResult> ConnectionClosingAsync(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result
    )
    {
        await PublishAsync(() => _efPending.TakeOn(connection)).ConfigureAwait(false);
        return result;
    }

    public void ConnectionClosed(DbConnection connection, ConnectionEndEventData eventData) =>
        _openedIn.Remove(connection);

    public Task ConnectionClosedAsync(DbConnection connection, ConnectionEndEventData eventData)
    {
        _openedIn.Remove(connection);
        return Task.CompletedTask;
    }

    private void RecordOpening(DbConnection connection, ConnectionEventData eventData)
    {
        if (
            eventData.Context is { } context
            && ContextTransaction.Of(context).Ambient is { } ambient
        )
        {
            _openedIn.AddOrUpdate(connection, ambient);
        }
        else
        {
            _openedIn.Remove(connection);
        }
    }

    // The transaction a command runs in, as the context has it, or else the ambient one its connection was opened in,
    // while that's still live.
    private ContextTransaction CommandTransaction(DbContext context)
    {
        var transaction = ContextTransaction.Of(context);
        return
            transaction.IsNone
            && _openedIn.TryGetValue(
                ContextTransaction.Connection(context).DbConnection,
                out var opened
            )
            && ContextTransaction.Active(opened)
            ? transaction with
            {
                Ambient = opened,
            }
            : transaction;
    }

    // Starts the save, and takes the one its context still holds, if it wrote anything: a save that never completed.
    // It runs inside this one (an interceptor saving again from SavedChanges), or a concurrency conflict or a throwing
    // interceptor ended it without an event.
    // A provider Sluice can't see transactions or commands of is refused before anything else happens.
    private SaveInProgress? Start(DbContext context)
    {
        ContextTransaction.Connection(context);
        var leftover = Take(_saves, context, keep: false);
        _saves.AddOrUpdate(context, new());
        return leftover is { Capture: not null } ? leftover : null;
    }

    private List<Dependency> Earlier(DbContext context, SaveInProgress? leftover) =>
        [
            .. ContextTransaction.Of(context).Ef is { } ef && ContextTransaction.Ended(ef)
                ? _efPending.Take(ef)?.All() ?? []
                : [],
            .. leftover is null ? [] : HandToLiveTransaction(context, leftover),
        ];

    // Publishes what a write wrote: when the transaction it ran in ends, or at once outside one.
    private void Complete(Write? write)
    {
        if (write is { } completed && Route(completed) is { } now)
        {
            Publish(now);
        }
    }

    private async Task CompleteAsync(Write? write)
    {
        if (write is { } completed && Route(completed) is { } now)
        {
            await PublishAsync(now).ConfigureAwait(false);
        }
    }

    // The save the context has in progress, if it wrote anything, taken unless kept. A save that reports rows written
    // without a command Sluice saw invalidated nothing, which is logged.
    private Write? Save(DbContext? context, bool keep, int rows = 0)
    {
        if (context is null || Take(_saves, context, keep) is not { } save)
        {
            return null;
        }

        if (save.Capture is not { } capture)
        {
            if (rows > 0)
            {
                LogNothingCaptured(rows, context.GetType().Name);
            }

            return null;
        }

        return new(context, capture.Dependencies, save.RolledBack, capture.Transaction.Ambient);
    }

    // Which rows a bulk write changed is unknown, so every read of the types it may have written is evicted. EF 10
    // reports ExecuteDelete as ExecuteUpdate (both pass CommandSource.BulkUpdate, its old name), so the statement says
    // which it was; anything but an UPDATE is taken for a delete, which also evicts the lists its rows were in and what
    // the database cascades to.
    // A keyed one (ForKey, ForKeys) names its rows: the pin the command took when EF initialised it.
    private Write? BulkWrite(DbCommand command, CommandEventData eventData)
    {
        if (
            eventData
            is not {
                Context: { } context,
                CommandSource: CommandSource.ExecuteUpdate or CommandSource.ExecuteDelete,
            }
        )
        {
            return null;
        }

        _bulk.TryGetValue(command, out var bulk);
        _bulk.Remove(command);
        return new(
            context,
            () => BulkWritten(context, command, bulk?.Pin),
            Undone: false,
            bulk?.Ambient
        );
    }

    // Only the pinned type narrows, and only when the statement names its table and the keys are within the limit;
    // every other type it names, and the pinned one past the limit, publish whole. The pinned type is compared by
    // root in the context's own model, so a pin from another model's query never matches.
    private static List<Dependency> BulkWritten(DbContext context, DbCommand command, KeyPin? pin)
    {
        var update = IsUpdate(command);
        var sql = command.CommandText;
        var keyed = pin is { Keys.Count: <= ModelSources.KeyLimit }
            ? pin.EntityType.GetRootType()
            : null;
        return
        [
            .. ModelSources
                .Named(context, sql)
                .SelectMany(entityType =>
                    entityType == keyed
                        ? update
                            ? ModelSources.KeyedUpdateDependencies(
                                context,
                                pin!.EntityType,
                                pin.Keys,
                                sql
                            )
                            : ModelSources.KeyedDeleteDependencies(pin!.EntityType, pin.Keys)
                        : update
                            ? ModelSources.UpdateDependencies(entityType)
                            : ModelSources.AllDependencies(entityType)
                ),
        ];
    }

    private static bool IsUpdate(DbCommand command) =>
        TrackedReads
            .FirstStatement(command)
            ?.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
            is true;

    // What to publish now, having handed the write to the transaction the context is in now, if any, to publish when
    // that ends; null when the transaction has all of it. The transaction is read here, not when the write began,
    // since an interceptor may have committed it already. The dependencies a transaction takes are derived now, before
    // its commit, where a throw is still safe, because a save's entries can change again before it.
    //
    // An EF transaction committed or rolled back outside EF (on the DbTransaction) has ended though EF still holds it,
    // so what it holds publishes now, with the write. A write whose context can't see the ambient transaction it ran
    // in (a TransactionScope that doesn't flow into async code) publishes now, since it may not be in it, and again
    // when that transaction completes, since it may.
    private Func<List<Dependency>>? Route(Write write)
    {
        var transaction = ContextTransaction.Of(write.Context);
        if (transaction.Ef is { } ef)
        {
            if (ContextTransaction.Ended(ef))
            {
                return () => [.. _efPending.Take(ef)?.All() ?? [], .. write.Derive()];
            }

            Defer(write.Context, transaction, write.Derive(), write.Undone);
            return null;
        }

        if (transaction.Ambient is { } ambient)
        {
            HandToAmbient(ambient, write.Derive());
            return null;
        }

        if (write.Recorded is not { } recorded || !ContextTransaction.Active(recorded))
        {
            return write.Derive;
        }

        var dependencies = write.Derive();
        try
        {
            HandToAmbient(recorded, dependencies);
        }
        catch (ObjectDisposedException)
        {
            // It has ended, and publishing now covers it.
        }

        if (Interlocked.Exchange(ref _loggedHiddenScope, 1) == 0)
        {
            LogHiddenScope();
        }

        return () => dependencies;
    }

    // Keeps a write for the EF transaction's end, and adds a probe of the transaction to the flow's box, so the guard
    // sees it from the flow's first write in it, even when it began before the box.
    private void Defer(
        DbContext context,
        ContextTransaction transaction,
        List<Dependency> dependencies,
        bool undone
    )
    {
        var connection = ContextTransaction.Connection(context);
        _efPending.For(transaction.Ef!, connection.DbConnection).Add(dependencies, undone);
        AddProbe(connection, transaction.EfId!.Value);
    }

    private void AddProbe(TransactionEventData eventData)
    {
        if (eventData.Context is { } context)
        {
            AddProbe(ContextTransaction.Connection(context), eventData.TransactionId);
        }
    }

    // Adds a probe of the EF transaction to the flow's SluiceFlow box, if it has one and doesn't hold one for it, so a
    // cached read on the flow sees what the transaction has written and not committed (SLUICE-EF-025). It's keyed and
    // checked by EF's id for the transaction, not the DbTransaction object, which may be handed out again for another
    // flow's transaction (Npgsql reuses it), and it reports only while the context's connection is still in that
    // transaction. It holds the connection weakly, so the box keeps neither the context nor the transaction alive.
    private void AddProbe(IRelationalConnection connection, Guid transactionId)
    {
        var held = new WeakReference<IRelationalConnection>(connection);
        FlowWrites.AddProbe(_cache, transactionId, () => PendingIn(held, transactionId));
    }

    // A transaction committed or rolled back outside EF has ended though EF still holds it: what it held publishes now,
    // once, and nothing is pending. Publishing never throws.
    private IReadOnlyCollection<Dependency> PendingIn(
        WeakReference<IRelationalConnection> held,
        Guid transactionId
    )
    {
        if (
            !held.TryGetTarget(out var live)
            || live.CurrentTransaction is not { } current
            || current.TransactionId != transactionId
        )
        {
            return [];
        }

        var transaction = current.GetDbTransaction();
        if (ContextTransaction.Ended(transaction))
        {
            Publish(() => _efPending.Take(transaction)?.All() ?? []);
            return [];
        }

        return _efPending.Find(transaction)?.Pending() ?? [];
    }

    // Publishes the write when the ambient transaction completes, and keeps it as pending until then. The core's
    // lookup finds it through the caller's Transaction.Current; an enlisted transaction that isn't the current one
    // (EnlistTransaction without a scope, or a scope that didn't flow here) is found through a probe in the flow's
    // box instead, one per transaction in each box.
    private void HandToAmbient(Transaction ambient, List<Dependency> dependencies)
    {
        PublishWhenCompleted(ambient, dependencies);

        var added = false;
        var pending = _ambientPending.GetValue(
            ambient,
            _ =>
            {
                added = true;
                return new();
            }
        );
        pending.Add(dependencies, undone: false);
        if (added)
        {
            ambient.TransactionCompleted += (_, _) => _ambientPending.Remove(ambient);
        }

        if (!ReferenceEquals(Transaction.Current, ambient))
        {
            AddProbe(ambient);
        }
    }

    // Reports what the enlisted transaction has pending until it completes, which removes it. Keyed by its local
    // identifier, and held weakly, as the EF transaction's probe holds its connection.
    private void AddProbe(Transaction ambient)
    {
        var held = new WeakReference<Transaction>(ambient);
        FlowWrites.AddProbe(
            _cache,
            ambient.TransactionInformation.LocalIdentifier,
            () =>
                held.TryGetTarget(out var live)
                && _ambientPending.TryGetValue(live, out var pending)
                    ? pending.Pending()
                    : []
        );
    }

    private CapturedSave Capture(DbContext context)
    {
        var save = CapturedSave.Capture(context, CommandTransaction(context));
        foreach (var entityType in save.Unversioned)
        {
            if (_loggedUnversioned.TryAdd(entityType.Name, true))
            {
                if (ModelSources.SingleTable(entityType))
                {
                    LogUnversioned(entityType.ShortName());
                }
                else
                {
                    LogSplitAcrossTables(entityType.ShortName());
                }
            }
        }

        return save;
    }

    // A save left over when its context saves again. Its transaction may have ended since, or be the one the next save
    // runs in, so it publishes now, and again from the transaction it ran in if that's still live, never from another
    // that has taken its place (an EF transaction is told apart by EF's id). Both only over-invalidate. An EF
    // transaction that ended outside EF publishes what it held too.
    private List<Dependency> HandToLiveTransaction(DbContext context, SaveInProgress leftover)
    {
        var capture = leftover.Capture!;
        var dependencies = capture.Dependencies();
        var then = capture.Transaction;
        var now = ContextTransaction.Of(context);
        if (then.EfId is { } id && now is { EfId: { } nowId, Ef: { } ef } && id == nowId)
        {
            if (ContextTransaction.Ended(ef))
            {
                dependencies.AddRange(_efPending.Take(ef)?.All() ?? []);
            }
            else
            {
                Defer(context, now, dependencies, leftover.RolledBack);
            }
        }
        else if (then.Ambient is { } ambient)
        {
            try
            {
                HandToAmbient(ambient, dependencies);
            }
            catch (ObjectDisposedException)
            {
                // It has ended, and publishing now covers it.
            }
        }

        return dependencies;
    }

    // Raised synchronously, maybe on another thread, and at once if the transaction has already completed. So it
    // publishes with the sync Invalidate and never throws: a throw skips the handlers after it, and escapes into
    // whatever completed the transaction.
    private void PublishWhenCompleted(Transaction ambient, List<Dependency> dependencies) =>
        ambient.TransactionCompleted += (_, _) => Publish(() => dependencies);

    // The value held for key, removed unless kept, or null when there's none.
    private static TValue? Take<TKey, TValue>(
        ConditionalWeakTable<TKey, TValue> table,
        TKey key,
        bool keep
    )
        where TKey : class
        where TValue : class
    {
        if (!table.TryGetValue(key, out var value))
        {
            return null;
        }

        if (!keep)
        {
            table.Remove(key);
        }

        return value;
    }

    // Takes the dependencies as a function so that a failure to derive them, after a commit without a transaction,
    // is logged too. An empty list publishes nothing.
    private void Publish(Func<List<Dependency>> derive)
    {
        List<Dependency>? dependencies = null;
        try
        {
            dependencies = derive();
            if (dependencies.Count > 0)
            {
                _cache.Invalidate(dependencies);
            }
        }
        catch (Exception exception)
        {
            LogPublishFailed(exception, dependencies?.Count);
        }
    }

    private async Task PublishAsync(Func<List<Dependency>> derive)
    {
        List<Dependency>? dependencies = null;
        try
        {
            dependencies = derive();
            if (dependencies.Count > 0)
            {
                await _cache.InvalidateAsync(dependencies).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            LogPublishFailed(exception, dependencies?.Count);
        }
    }

    // The count is null when deriving the dependencies failed.
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Error,
        Message = "Sluice couldn't invalidate the cached values that depend on an EF write; they stay cached until their duration ends. Dependencies: {DependencyCount}"
    )]
    private partial void LogPublishFailed(Exception exception, int? dependencyCount);

    // So that losing Children and unique-column precision is a choice, not a surprise: a busy type evicts every such
    // list and value on every write.
    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "Sluice evicts every read.Children list under {EntityType}'s foreign keys and every read.Entity of it by a unique column, whichever of those it has, whatever the parent or value, each time it saves a change to one other than an insert, because {EntityType} has no concurrency token the database changes on every write: the parent or value a row had when it was loaded may not be the one it leaves. To evict only the lists and values it leaves, map one (on PostgreSQL, xmin as a row version; on SQL Server, a rowversion column), or make each such foreign key and unique column a concurrency token."
    )]
    private partial void LogUnversioned(string entityType);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Information,
        Message = "Sluice evicts every read.Children list under {EntityType}'s foreign keys and every read.Entity of it by a unique column, whichever of those it has, whatever the parent or value, each time it saves a change to one other than an insert, because {EntityType} is stored across several tables (table-per-type or entity splitting), so no concurrency token covers every column EF writes: the parent or value a row had when it was loaded may not be the one it leaves."
    )]
    private partial void LogSplitAcrossTables(string entityType);

    // Once per cache: the remedy is in the app's code, and a busy app would log it on every write.
    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Warning,
        Message = "Sluice saw an EF write's TransactionScope when its connection was opened or its first command ran, but not where the write completed, so it invalidated what the write changed at once, before the scope completes, and does so again when the scope completes: a read in between caches the old value until then. Create the TransactionScope with TransactionScopeAsyncFlowOption.Enabled, so it flows into the async code that completes the write. Logged once."
    )]
    private partial void LogHiddenScope();

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Error,
        Message = "Sluice saw no command for a save on {Context} that reported {Rows} rows written, so it invalidated nothing for it, and what depends on those rows stays cached until its duration ends. Sluice reads what a save writes from the save's first command (CommandSource.SaveChanges), so a provider or interceptor that writes without one isn't supported."
    )]
    private partial void LogNothingCaptured(int rows, string context);

    // A write that ended on a context, with how to derive what it may have changed, whether a rollback to a savepoint
    // undid it, and the ambient transaction it ran in when its context may not see it where it completes.
    private readonly record struct Write(
        DbContext Context,
        Func<List<Dependency>> Derive,
        bool Undone,
        Transaction? Recorded
    );

    // What a bulk command took when EF initialised it.
    private sealed record BulkCommand(KeyPin? Pin, Transaction? Ambient);

    // A save from SavingChanges until EF reports how it ended: what it writes, once its first command is initialised,
    // and whether a rollback to a savepoint made during it has undone that. Read and set on the save's own flow.
    private sealed class SaveInProgress
    {
        public CapturedSave? Capture { get; set; }

        public bool RolledBack { get; set; }
    }

    // What EF transactions have pending, each by its DbTransaction, which contexts sharing a transaction have in
    // common, and found by the connection it runs on too, since EF disposes a transaction without an event. Weak,
    // because a transaction disposed without commit or rollback raises no event either.
    private sealed class EfPending
    {
        private readonly ConditionalWeakTable<DbTransaction, PendingWrites> _byTransaction = new();
        private readonly ConditionalWeakTable<DbConnection, HashSet<DbTransaction>> _byConnection =
            new();

        public PendingWrites For(DbTransaction transaction, DbConnection connection)
        {
            var onConnection = _byConnection.GetValue(connection, _ => []);
            lock (onConnection)
            {
                onConnection.Add(transaction);
            }

            return _byTransaction.GetValue(transaction, _ => new() { Connection = connection });
        }

        public PendingWrites? Find(DbTransaction transaction) =>
            _byTransaction.TryGetValue(transaction, out var pending) ? pending : null;

        // Only one caller takes what a transaction has pending, so only one publishes it.
        public PendingWrites? Take(DbTransaction transaction)
        {
            if (
                !_byTransaction.TryGetValue(transaction, out var pending)
                || !_byTransaction.Remove(transaction)
            )
            {
                return null;
            }

            if (_byConnection.TryGetValue(pending.Connection!, out var onConnection))
            {
                lock (onConnection)
                {
                    onConnection.Remove(transaction);
                }
            }

            return pending;
        }

        // Everything the connection's transactions have pending, taken.
        public List<Dependency> TakeOn(DbConnection connection)
        {
            if (!_byConnection.TryGetValue(connection, out var onConnection))
            {
                return [];
            }

            DbTransaction[] transactions;
            lock (onConnection)
            {
                transactions = [.. onConnection];
            }

            return [.. transactions.SelectMany(transaction => Take(transaction)?.All() ?? [])];
        }
    }

    // What one transaction's writes publish when it ends. Its own writes add one at a time, since a transaction's
    // connection runs one command at a time, but a flow that fans out reads it through its probe from other tasks,
    // meanwhile, so both take the lock, and a read gets a copy.
    private sealed class PendingWrites
    {
        private readonly Lock _lock = new();

        // Written and not undone, which the probe reports.
        private readonly List<Dependency> _pending = [];

        // Undone by a rollback to a savepoint: published when the transaction ends, never pending.
        private readonly List<Dependency> _undone = [];

        // The savepoints EF made, innermost last: how much was pending when each was made, and the save its context
        // had in progress then.
        private readonly List<(int At, SaveInProgress? During)> _savepoints = [];

        // The connection an EF transaction runs on; none for an ambient one.
        public DbConnection? Connection { get; init; }

        public void Add(List<Dependency> dependencies, bool undone)
        {
            lock (_lock)
            {
                (undone ? _undone : _pending).AddRange(dependencies);
            }
        }

        public List<Dependency> Pending()
        {
            lock (_lock)
            {
                return [.. _pending];
            }
        }

        public List<Dependency> All()
        {
            lock (_lock)
            {
                return [.. _pending, .. _undone];
            }
        }

        public void Save(SaveInProgress? during)
        {
            lock (_lock)
            {
                _savepoints.Add((_pending.Count, during));
            }
        }

        // Undoes what was written since the innermost savepoint, which stays, and returns the save in progress when it
        // was made; null when there's none.
        public SaveInProgress? RollBack()
        {
            lock (_lock)
            {
                if (_savepoints is not [.., var (at, during)])
                {
                    return null;
                }

                _undone.AddRange(_pending[at..]);
                _pending.RemoveRange(at, _pending.Count - at);
                return during;
            }
        }

        public void Release()
        {
            lock (_lock)
            {
                if (_savepoints.Count > 0)
                {
                    _savepoints.RemoveAt(_savepoints.Count - 1);
                }
            }
        }
    }
}
