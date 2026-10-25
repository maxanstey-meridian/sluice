using System.Collections.Concurrent;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
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

    // What saves and bulk writes inside an enlisted or ambient transaction wrote, until it completes: each publishes from
    // its own TransactionCompleted handler, so this is only for the uncommitted-write guard to read (SLUICE-EF-025).
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

    // Whether this instance has registered its hooks with the cache, which For does once, under the lock.
    private volatile bool _registered;
    private readonly Lock _registering = new();

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

        interceptor.Register();
        return interceptor;
    }

    /// <summary>
    /// Publishes what <paramref name="transaction"/> still has pending, for one of EF's diagnostic events, which EF
    /// raises for every context, interceptors or not: a commit or rollback (<c>TransactionCommitted</c>,
    /// <c>TransactionRolledBack</c>), which a context without <c>UseSluice</c> may run on a transaction a
    /// <c>UseSluice</c> context shares through <c>UseTransaction</c>, and the disposal of a transaction EF began
    /// (<c>TransactionDisposed</c>), which no interceptor sees: EF disposes it when the app does, and when it resets its
    /// context (disposed, or returned to the pool), whoever owns the connection. One committed or rolled back outside EF
    /// has nothing else to publish it on a connection EF doesn't close. EF has ended the transaction by then. Every
    /// cache's interceptor looks, since the event names no cache, but those on <paramref name="seenBy"/>, which see
    /// the event themselves, and publish it on the caller's path; at most one holds the transaction.
    /// </summary>
    internal static void TransactionEnded(DbTransaction transaction, DbContext? seenBy)
    {
        foreach (var interceptor in Others(seenBy))
        {
            interceptor.Publish(() => interceptor._efPending.TakeAll(transaction));
        }
    }

    /// <summary>
    /// Publishes what <paramref name="transaction"/> has pending, and keeps it, for EF's <c>TransactionError</c>
    /// diagnostic event, as <see cref="TransactionFailed"/> does for a context with <c>UseSluice</c>.
    /// </summary>
    internal static void TransactionErred(DbTransaction transaction, DbContext? seenBy)
    {
        foreach (var interceptor in Others(seenBy))
        {
            interceptor.Publish(() => interceptor._efPending.Find(transaction)?.All() ?? []);
        }
    }

    // Every cache's interceptor but those on the context, which see its transaction events themselves. A context
    // already disposed (the app ending a transaction it kept) can't say, so every one looks: only one takes what the
    // transaction holds. Never throws, since EF raises the event inside the call that ended the transaction.
    private static IEnumerable<SluiceInterceptor> Others(DbContext? context)
    {
        IEnumerable<IInterceptor>? own;
        try
        {
            own = context
                ?.GetService<IDbContextOptions>()
                .FindExtension<CoreOptionsExtension>()
                ?.Interceptors;
        }
        catch (ObjectDisposedException)
        {
            own = null;
        }

        foreach (var (_, interceptor) in _instances)
        {
            if (own?.Contains(interceptor) != true)
            {
                yield return interceptor;
            }
        }
    }

    // From the instance the table kept, not its constructor: GetValue can construct one per racing caller and keep
    // only one, and the cache holds one lookup, the last registered. Once per cache, since For runs on every options
    // build (every request, with scoped options); no caller gets the interceptor before its hooks are registered.
    private void Register()
    {
        if (_registered)
        {
            return;
        }

        lock (_registering)
        {
            if (!_registered)
            {
                FlowWrites.OnAmbient(_cache, _ambientWrites);
                _registered = true;
            }
        }
    }

    // What the save writes is read at its first command, after every SavingChanges interceptor and EF's own change
    // detection and cascades, so this only starts the save, and publishes what's left of earlier ones: one its context
    // left unfinished, and what its EF transaction held, if that was committed or rolled back outside EF. A provider
    // may refuse the save's commands in such a transaction (Npgsql does), so this may be the last Sluice sees of it
    // until the transaction is disposed, or its connection begins or joins another, opens, closes or is disposed.
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

    // A command on a context whose EF transaction was committed or rolled back outside EF publishes what the
    // transaction held, as a save's start does: a bulk write has no start, and a provider may refuse the command as EF
    // gives it the ended transaction (Npgsql does), before EF raises any other event for it. Sync, since EF raises
    // only this, from async code too; it publishes once per such transaction.
    public InterceptionResult<DbCommand> CommandCreating(
        CommandCorrelatedEventData eventData,
        InterceptionResult<DbCommand> result
    )
    {
        if (
            eventData.Context is { } context
            && ContextTransaction.Connection(context).CurrentTransaction?.GetDbTransaction()
                is { } ef
            && ContextTransaction.Ended(ef)
        )
        {
            Publish(() => _efPending.TakeAll(ef));
        }

        return result;
    }

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
    // Both refuse here, before anything is written, a model whose converters Sluice can't trust, or one of whose source
    // names the core refuses, and build every source of the model, so publishing builds none.
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
                ModelSources.EnsureSources(context.Model);
                _bulk.AddOrUpdate(result, new(KeyPin.Take(), CommandTransaction(context).Ambient));
                break;
        }

        return result;
    }

    // A connection runs one transaction at a time, so anything still pending on the connection when it starts another
    // belongs to one that ended without a commit or rollback EF saw: one committed or rolled back outside EF, or
    // disposed without either, or one whose commit failed. It publishes now, since the first may have committed it,
    // and over-invalidating the others is safe. So does anything pending on the DbTransaction object it returns: one
    // that has just begun has written nothing, and a provider may hand the object out again, on the same connection or
    // another (Npgsql does: the object belongs to the physical connection, which the pool hands to whichever
    // DbConnection opens next; SqlClient and Sqlite make a new one).
    public DbTransaction TransactionStarted(
        DbConnection connection,
        TransactionEndEventData eventData,
        DbTransaction result
    )
    {
        Publish(() => Starting(connection, result));
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
        await PublishAsync(() => Starting(connection, result)).ConfigureAwait(false);
        AddProbe(eventData);
        return result;
    }

    // EF checks that the transaction is on the context's connection only after its started and used events, and
    // refuses one that isn't: one a TransactionStarting interceptor took from another connection, or the app passed to
    // UseTransaction, which is live there, and leaves the context's own as it was. So none of it publishes.
    private List<Dependency> Starting(DbConnection connection, DbTransaction transaction) =>
        ContextTransaction.On(transaction, connection)
            ? [.. _efPending.TakeOn(connection), .. _efPending.TakeAll(transaction)]
            : [];

    // A context joining a transaction (UseTransaction) on the connection that has it shares what it has pending, so it
    // keeps it, and publishes what any other on the connection has pending, as starting one does. What the
    // DbTransaction object holds from another connection belongs to a transaction that has ended, since a transaction
    // runs on one connection and the object is on this one now, so that publishes too. One EF refuses publishes
    // nothing, as starting one doesn't.
    public DbTransaction TransactionUsed(
        DbConnection connection,
        TransactionEventData eventData,
        DbTransaction result
    )
    {
        Publish(() => Joining(connection, result));
        AddProbe(eventData);
        return result;
    }

    public async ValueTask<DbTransaction> TransactionUsedAsync(
        DbConnection connection,
        TransactionEventData eventData,
        DbTransaction result,
        CancellationToken cancellationToken = default
    )
    {
        await PublishAsync(() => Joining(connection, result)).ConfigureAwait(false);
        AddProbe(eventData);
        return result;
    }

    private List<Dependency> Joining(DbConnection connection, DbTransaction transaction) =>
        ContextTransaction.On(transaction, connection)
            ?
            [
                .. _efPending.TakeOn(connection, keep: other => other == transaction),
                .. _efPending.TakeUnlessOn(transaction, connection),
            ]
            : [];

    public void TransactionCommitted(
        DbTransaction transaction,
        TransactionEndEventData eventData
    ) => Publish(() => _efPending.TakeAll(transaction));

    public Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default
    ) => PublishAsync(() => _efPending.TakeAll(transaction));

    // A rollback publishes too: over-invalidating is safe.
    public void TransactionRolledBack(
        DbTransaction transaction,
        TransactionEndEventData eventData
    ) => Publish(() => _efPending.TakeAll(transaction));

    public Task TransactionRolledBackAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default
    ) => PublishAsync(() => _efPending.TakeAll(transaction));

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
    public void CreatedSavepoint(DbTransaction transaction, TransactionEventData eventData)
    {
        var ended = CreateSavepoint(transaction, eventData);
        Publish(() => ended);
    }

    public Task CreatedSavepointAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        CancellationToken cancellationToken = default
    )
    {
        var ended = CreateSavepoint(transaction, eventData);
        return PublishAsync(() => ended);
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

    // Notes the save the context has in progress: EF makes its own savepoint before the save's first command, so a
    // rollback to it undoes everything that save writes, whenever the save gets to publish. Like the first command,
    // it starts the save's record when there's none: a save run inside this one (an interceptor saving again from
    // SavingChanges) took it, and completed. One the app makes outside a save starts a record with nothing captured,
    // which the next save drops. Returns what an earlier transaction left on the DbTransaction object, to publish.
    private List<Dependency> CreateSavepoint(
        DbTransaction transaction,
        TransactionEventData eventData
    )
    {
        if (transaction.Connection is not { } connection)
        {
            return [];
        }

        _efPending
            .For(transaction, connection, out var ended)
            .Save(eventData.Context is { } context ? _saves.GetValue(context, _ => new()) : null);
        return ended;
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
    //
    // A closed connection holds no transaction, so what any still has pending on it publishes, as closing does: one
    // committed or rolled back outside EF on a connection EF closed without an event, which it does when it resets a
    // pooled context for its next lease.
    public InterceptionResult ConnectionOpening(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result
    )
    {
        Publish(() => _efPending.TakeOn(connection));
        RecordOpening(connection, eventData);
        return result;
    }

    public async ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default
    )
    {
        await PublishAsync(() => _efPending.TakeOn(connection)).ConfigureAwait(false);
        RecordOpening(connection, eventData);
        return result;
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

    // Disposing a context closes a connection EF owns without raising ConnectionClosing, even one the app opened
    // (Database.OpenConnection), and then disposes it, which EF does raise this for.
    public InterceptionResult ConnectionDisposing(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result
    )
    {
        Publish(() => _efPending.TakeOn(connection));
        return result;
    }

    public async ValueTask<InterceptionResult> ConnectionDisposingAsync(
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

    // EF resets a pooled context for its next lease (PoolReset) without an event for a transaction passed to
    // UseTransaction, which it lets go of, nor for closing a connection it owns. So what the context's connection holds
    // of transactions that have ended publishes then: one committed or rolled back outside EF, or one closing the
    // connection ended. One still live on it, on a connection the app owns, which EF leaves open, or before EF closes
    // its own, keeps what it holds.
    internal void Reset(IRelationalConnection connection) => Publish(() => EndedOn(connection));

    internal Task ResetAsync(IRelationalConnection connection) =>
        PublishAsync(() => EndedOn(connection));

    private List<Dependency> EndedOn(IRelationalConnection connection)
    {
        var db = connection.DbConnection;
        return _efPending.TakeOn(
            db,
            keep: transaction =>
                !ContextTransaction.Ended(transaction) && ContextTransaction.On(transaction, db)
        );
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

    // What the context's EF transaction held, if that ended outside EF, and the leftover save's writes.
    private List<Dependency> Earlier(DbContext context, SaveInProgress? leftover) =>
        [
            .. ContextTransaction.Of(context).Ef is { } ef && ContextTransaction.Ended(ef)
                ? _efPending.TakeAll(ef)
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
    // so what it holds publishes now, with the write. One whose DbTransaction object the provider has handed to another
    // connection (ContextTransaction.On) is that connection's now, and the write ran outside it, so the write publishes
    // now, with what the object holds from the context's own connection, whose transaction has ended, and the other
    // connection's transaction keeps what it holds. A write whose context can't see the ambient transaction it ran
    // in (a TransactionScope that doesn't flow into async code) publishes now, since it may not be in it, and again
    // when that transaction completes, since it may.
    private Func<List<Dependency>>? Route(Write write)
    {
        var transaction = ContextTransaction.Of(write.Context);
        if (transaction.Ef is { } ef)
        {
            if (ContextTransaction.Ended(ef))
            {
                return () => [.. _efPending.TakeAll(ef), .. write.Derive()];
            }

            var connection = ContextTransaction.Connection(write.Context).DbConnection;
            if (!ContextTransaction.On(ef, connection))
            {
                return () => [.. _efPending.TakeMadeOn(ef, connection), .. write.Derive()];
            }

            var ended = Defer(write.Context, transaction, write.Derive(), write.Undone);
            return ended.Count > 0 ? () => ended : null;
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
    // sees it from the flow's first write in it, even when it began before the box. Returns what an earlier
    // transaction left on the DbTransaction object from another connection, to publish now.
    private List<Dependency> Defer(
        DbContext context,
        ContextTransaction transaction,
        List<Dependency> dependencies,
        bool undone
    )
    {
        var connection = ContextTransaction.Connection(context);
        _efPending
            .For(transaction.Ef!, connection.DbConnection, out var ended)
            .Add(dependencies, undone);
        AddProbe(connection, transaction.EfId!.Value);
        return ended;
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
    // once, and nothing is pending. Publishing never throws. One whose DbTransaction object the provider has handed to
    // another connection (ContextTransaction.On) is that connection's, and what it has pending isn't this flow's; what
    // it holds from this flow's connection belongs to a transaction that has ended, so that publishes, as Route does.
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
            Publish(() => _efPending.TakeAll(transaction));
            return [];
        }

        if (!ContextTransaction.On(transaction, live.DbConnection))
        {
            Publish(() => _efPending.TakeMadeOn(transaction, live.DbConnection));
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
    // that has taken its place (an EF transaction is told apart by EF's id), nor from one that ended outside EF,
    // which Earlier publishes, or moved to another connection with its DbTransaction object (Route). Both only
    // over-invalidate.
    private List<Dependency> HandToLiveTransaction(DbContext context, SaveInProgress leftover)
    {
        var capture = leftover.Capture!;
        var dependencies = capture.Dependencies();
        var then = capture.Transaction;
        var now = ContextTransaction.Of(context);
        if (then.EfId is { } id && now is { EfId: { } nowId, Ef: { } ef } && id == nowId)
        {
            if (
                !ContextTransaction.Ended(ef)
                && ContextTransaction.On(ef, ContextTransaction.Connection(context).DbConnection)
            )
            {
                return [.. dependencies, .. Defer(context, now, dependencies, leftover.RolledBack)];
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
    // common, and found by the connection it runs on too, since EF ends a transaction without an event. Each is held
    // until EF commits or rolls it back, its DbTransaction object begins again or is joined on another connection, or
    // its connection starts or joins another, opens, closes or is disposed: a connection runs one transaction at a
    // time, and holds none while closed. A record belongs to the connection it was made on: a provider may hand the
    // object out again on another (Npgsql does, through its pool), so the object's next writes there take the old record
    // and start one of their own, and a connection's set holds exactly the objects whose records were made on it. One
    // lock for both tables, so a record can't be taken through one while it's added to through the other. Weak by
    // connection, so a connection dropped without any of those leaves nothing behind.
    private sealed class EfPending
    {
        private readonly Lock _lock = new();
        private readonly ConditionalWeakTable<DbTransaction, PendingWrites> _byTransaction = new();
        private readonly ConditionalWeakTable<DbConnection, HashSet<DbTransaction>> _byConnection =
            new();

        // The record for the transaction's writes on the connection, and in ended what the object held from another
        // connection, taken: a transaction that has ended, since one runs on one connection.
        public PendingWrites For(
            DbTransaction transaction,
            DbConnection connection,
            out List<Dependency> ended
        )
        {
            lock (_lock)
            {
                ended = UnlessOn(transaction, connection);
                _byConnection.GetValue(connection, _ => []).Add(transaction);
                return _byTransaction.GetValue(transaction, _ => new() { Connection = connection });
            }
        }

        public PendingWrites? Find(DbTransaction transaction) =>
            _byTransaction.TryGetValue(transaction, out var pending) ? pending : null;

        // Everything the transaction has pending, taken; none when another caller took it, so only one publishes it.
        public List<Dependency> TakeAll(DbTransaction transaction)
        {
            lock (_lock)
            {
                return Remove(transaction)?.All() ?? [];
            }
        }

        // Everything the transaction has pending from a connection other than this one, taken.
        public List<Dependency> TakeUnlessOn(DbTransaction transaction, DbConnection connection)
        {
            lock (_lock)
            {
                return UnlessOn(transaction, connection);
            }
        }

        // What the transaction has pending from this connection, taken.
        public List<Dependency> TakeMadeOn(DbTransaction transaction, DbConnection connection)
        {
            lock (_lock)
            {
                return Find(transaction) is { } pending && pending.Connection == connection
                    ? Remove(transaction)!.All()
                    : [];
            }
        }

        // Everything the connection's transactions have pending, but those kept, taken.
        public List<Dependency> TakeOn(
            DbConnection connection,
            Func<DbTransaction, bool>? keep = null
        )
        {
            if (!_byConnection.TryGetValue(connection, out _))
            {
                return [];
            }

            lock (_lock)
            {
                if (!_byConnection.TryGetValue(connection, out var onConnection))
                {
                    return [];
                }

                return
                [
                    .. onConnection
                        .Where(transaction => keep?.Invoke(transaction) != true)
                        .ToList()
                        .SelectMany(transaction => Remove(transaction)!.All()),
                ];
            }
        }

        // Under the lock.
        private List<Dependency> UnlessOn(DbTransaction transaction, DbConnection connection) =>
            Find(transaction) is { } pending && pending.Connection != connection
                ? Remove(transaction)!.All()
                : [];

        // Under the lock.
        private PendingWrites? Remove(DbTransaction transaction)
        {
            if (!_byTransaction.TryGetValue(transaction, out var pending))
            {
                return null;
            }

            _byTransaction.Remove(transaction);
            if (_byConnection.TryGetValue(pending.Connection!, out var onConnection))
            {
                onConnection.Remove(transaction);
            }

            return pending;
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
