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
        IDbCommandInterceptor
{
    private static readonly ConditionalWeakTable<SluiceCache, SluiceInterceptor> _instances = new();

    private readonly SluiceCache _cache;
    private readonly ILogger _logger;

    // A save in progress, from SavingChanges until EF reports how it ended. Weak, so a context that's dropped
    // mid-save leaves nothing behind.
    private readonly ConditionalWeakTable<DbContext, CapturedSave> _saves = new();

    // The keys a keyed bulk write (ForKey, ForKeys) pinned, held against the command that runs it from when EF
    // initialises it until it ends. Weak, so a command EF drops leaves nothing behind.
    private readonly ConditionalWeakTable<DbCommand, KeyPin> _pins = new();

    // What saves inside an EF transaction wrote, published when the transaction ends. Keyed by the DbTransaction,
    // which contexts sharing a transaction have in common. Weak, because a transaction disposed without commit or
    // rollback raises no event, and committed nothing.
    private readonly ConditionalWeakTable<DbTransaction, PendingWrites> _pending = new();

    // What saves inside an enlisted or ambient transaction wrote, until it completes: each publishes from its own
    // TransactionCompleted handler, so this is only for the uncommitted-write guard to read (SLUICE-EF-025).
    private readonly ConditionalWeakTable<Transaction, PendingWrites> _ambientPending = new();

    // How the core reads an ambient transaction's pending writes, made once so registering it again is the same value.
    private readonly Func<Transaction, IReadOnlyCollection<Dependency>> _ambientWrites;

    // Each entity type that has logged invalidating its foreign keys and unique columns whole, so each logs once per
    // cache.
    private readonly ConcurrentDictionary<string, bool> _loggedUnversioned = new();

    private SluiceInterceptor(SluiceCache cache, ILogger<SluiceInterceptor> logger)
    {
        _cache = cache;
        _logger = logger;
        _ambientWrites = transaction =>
            _ambientPending.TryGetValue(transaction, out var pending) ? pending.Snapshot() : [];
    }

    /// <summary>The interceptor for the <see cref="SluiceCache"/> registered in <paramref name="services"/>.</summary>
    /// <exception cref="InvalidOperationException">Sluice isn't registered in <paramref name="services"/>.</exception>
    internal static SluiceInterceptor For(IServiceProvider services)
    {
        var cache =
            services.GetService<SluiceCache>()
            ?? throw new InvalidOperationException(
                "UseSluice needs Sluice registered in the application's services: call services.AddSluice() "
                    + "where you register your services."
            );
        var interceptor = _instances.GetValue(
            cache,
            cache => new(cache, services.GetRequiredService<ILogger<SluiceInterceptor>>())
        );

        // From the instance the table kept, not its constructor: GetValue can construct one per racing caller and keep
        // only one, and the cache holds one lookup, the last registered. Every call registers the kept one's.
        FlowWrites.OnAmbient(cache, interceptor._ambientWrites);
        return interceptor;
    }

    public InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result
    )
    {
        if (eventData.Context is { } context)
        {
            PublishLeftover(context);
            _saves.AddOrUpdate(context, Capture(context));
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
            await PublishLeftoverAsync(context).ConfigureAwait(false);
            _saves.AddOrUpdate(context, Capture(context));
        }

        return result;
    }

    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Complete(Save(eventData.Context, keep: false));
        return result;
    }

    // The token is ignored throughout: a request cancelled after its write committed must still invalidate.
    public async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default
    )
    {
        await CompleteAsync(Save(eventData.Context, keep: false)).ConfigureAwait(false);
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
    // database cascades from each row it deletes, so it publishes nothing, and leaves nothing for a transaction.
    public int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        if (result != 0)
        {
            Complete(BulkWrite(command, eventData));
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
        if (result != 0)
        {
            await CompleteAsync(BulkWrite(command, eventData)).ConfigureAwait(false);
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

    // A provider may hand out the same DbTransaction object again (Npgsql does, per connection). Anything still
    // pending on it belongs to an earlier transaction that ended without a commit or rollback EF saw succeed: one
    // disposed without either, which committed nothing, or one whose commit failed, which has published already.
    public DbTransaction TransactionStarted(
        DbConnection connection,
        TransactionEndEventData eventData,
        DbTransaction result
    )
    {
        _pending.Remove(result);
        AddProbe(eventData);
        return result;
    }

    public ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection,
        TransactionEndEventData eventData,
        DbTransaction result,
        CancellationToken cancellationToken = default
    )
    {
        _pending.Remove(result);
        AddProbe(eventData);
        return new(result);
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
    ) => Publish(() => Take(_pending, transaction, keep: false)?.Snapshot() ?? []);

    public Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default
    ) => PublishAsync(() => Take(_pending, transaction, keep: false)?.Snapshot() ?? []);

    // A rollback publishes too: over-invalidating is safe.
    public void TransactionRolledBack(
        DbTransaction transaction,
        TransactionEndEventData eventData
    ) => Publish(() => Take(_pending, transaction, keep: false)?.Snapshot() ?? []);

    public Task TransactionRolledBackAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default
    ) => PublishAsync(() => Take(_pending, transaction, keep: false)?.Snapshot() ?? []);

    // A failed commit's outcome is unknown, so it publishes. EF also reports a savepoint that failed to release
    // here and carries on, and the transaction may still commit more after it, so the pending writes are kept for
    // its commit or rollback to publish again.
    public void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) =>
        Publish(() => Take(_pending, transaction, keep: true)?.Snapshot() ?? []);

    public Task TransactionFailedAsync(
        DbTransaction transaction,
        TransactionErrorEventData eventData,
        CancellationToken cancellationToken = default
    ) => PublishAsync(() => Take(_pending, transaction, keep: true)?.Snapshot() ?? []);

    // Publishes what a write wrote: when the transaction it ran in ends, or at once outside one.
    private void Complete(Write? write)
    {
        if (write is var (context, derive) && !DeferToTransaction(context, derive))
        {
            Publish(derive);
        }
    }

    private async Task CompleteAsync(Write? write)
    {
        if (write is var (context, derive) && !DeferToTransaction(context, derive))
        {
            await PublishAsync(derive).ConfigureAwait(false);
        }
    }

    // The save the context has in progress, if any, taken unless kept.
    private Write? Save(DbContext? context, bool keep) =>
        context is not null && Take(_saves, context, keep) is { } save
            ? new(context, save.Dependencies)
            : null;

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

        _pins.TryGetValue(command, out var pin);
        _pins.Remove(command);
        return new(context, () => BulkWritten(context, command, pin));
    }

    // The first bulk command EF initialises on the flow while a keyed call runs takes its pin: the statement the call
    // runs, initialised before any interceptor's NonQueryExecuting could start another, and before any command ends, so
    // the pin goes to it whatever order interceptors run in and whatever its row count. A later command on the flow
    // (another bulk write an interceptor runs, a retry by the execution strategy) finds the pin taken, and publishes
    // whole.
    public DbCommand CommandInitialized(CommandEndEventData eventData, DbCommand result)
    {
        if (
            eventData.CommandSource is CommandSource.ExecuteUpdate or CommandSource.ExecuteDelete
            && KeyPin.Take() is { } pin
        )
        {
            _pins.AddOrUpdate(result, pin);
        }

        return result;
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

    // Hands the write to the transaction the context is in now, if any, to publish when that ends: read here, not
    // when a save began, since an interceptor may have committed the save's transaction already. Its dependencies
    // are derived now, before the commit, where a throw is still safe, because a save's entries can change again
    // before it.
    private bool DeferToTransaction(DbContext context, Func<List<Dependency>> derive)
    {
        var transaction = ContextTransaction.Of(context);
        if (transaction.Ef is { } ef)
        {
            _pending.GetValue(ef, _ => new()).Add(derive());
            return true;
        }

        if (transaction.Ambient is { } ambient)
        {
            HandToAmbient(ambient, derive());
            return true;
        }

        return false;
    }

    // Adds a probe of the transaction to the flow's SluiceFlow box, if it has one, so a cached read on the flow sees
    // what the transaction has written and not committed (SLUICE-EF-025). It reports the pending writes only while the
    // context is still in this EF transaction, told apart by EF's id for it: the DbTransaction object may be handed out
    // again for another flow's transaction (Npgsql reuses it), and a transaction disposed without commit or rollback,
    // which committed nothing, raises no event. It holds the context's connection weakly, so the box keeps neither
    // the context nor the transaction alive.
    private void AddProbe(TransactionEventData eventData)
    {
        if (eventData.Context is not { } context)
        {
            return;
        }

        var connection = new WeakReference<IRelationalConnection>(
            ContextTransaction.Connection(context)
        );
        var transactionId = eventData.TransactionId;
        FlowWrites.AddProbe(
            _cache,
            () =>
                connection.TryGetTarget(out var live)
                && live.CurrentTransaction is { } current
                && current.TransactionId == transactionId
                && _pending.TryGetValue(current.GetDbTransaction(), out var pending)
                    ? pending.Snapshot()
                    : []
        );
    }

    // Publishes the write when the ambient transaction completes, and keeps it as pending until then. The core's
    // lookup finds it through the caller's Transaction.Current; an enlisted transaction that isn't the current one
    // (EnlistTransaction without a scope) is found through a probe in the flow's box instead, added once per
    // transaction, so nothing is reported twice.
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
        pending.Add(dependencies);
        if (added)
        {
            ambient.TransactionCompleted += (_, _) => _ambientPending.Remove(ambient);
        }

        if (!ReferenceEquals(Transaction.Current, ambient))
        {
            pending.ProbeOnce(() => AddProbe(ambient));
        }
    }

    // Reports what the enlisted transaction has pending until it completes, which removes it. Held weakly, as the EF
    // transaction's probe holds its connection.
    private bool AddProbe(Transaction ambient)
    {
        var held = new WeakReference<Transaction>(ambient);
        return FlowWrites.AddProbe(
            _cache,
            () =>
                held.TryGetTarget(out var live)
                && _ambientPending.TryGetValue(live, out var pending)
                    ? pending.Snapshot()
                    : []
        );
    }

    private CapturedSave Capture(DbContext context)
    {
        var save = CapturedSave.Capture(context);
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

    // A save still held when its context saves again never completed: this save runs inside it (an interceptor saving
    // again), or a concurrency conflict or a throwing interceptor ended it without an event. Its transaction may have
    // ended since, or be the one the next save runs in, so it publishes now, and again from the transaction it began
    // in if that's still live. Both only over-invalidate; nested inside SavingChanges, its rows are still pending, and
    // the inner save captures and publishes them again after its commit.
    private void PublishLeftover(DbContext context)
    {
        if (Take(_saves, context, keep: false) is { } leftover)
        {
            Publish(() => HandToLiveTransaction(context, leftover));
        }
    }

    private async Task PublishLeftoverAsync(DbContext context)
    {
        if (Take(_saves, context, keep: false) is { } leftover)
        {
            await PublishAsync(() => HandToLiveTransaction(context, leftover))
                .ConfigureAwait(false);
        }
    }

    // The leftover's dependencies, also handed to the transaction it began in when that's still live. An EF
    // transaction is live if the context is still in it: the same object (a provider may reuse it for a new
    // transaction, which then publishes it once more). A disposed ambient transaction refuses the handler.
    private List<Dependency> HandToLiveTransaction(DbContext context, CapturedSave leftover)
    {
        var dependencies = leftover.Dependencies();
        if (
            leftover.Transaction.Ef is { } ef
            && ReferenceEquals(ef, ContextTransaction.Of(context).Ef)
        )
        {
            _pending.GetValue(ef, _ => new()).Add(dependencies);
        }
        else if (leftover.Transaction.Ambient is { } ambient)
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

    // A write that ended on a context, with how to derive what it may have changed.
    private readonly record struct Write(DbContext Context, Func<List<Dependency>> Derive);

    // What one transaction's writes publish when it ends. Its own writes add one at a time, since a transaction's
    // connection runs one command at a time, but a flow that fans out reads it through its probe from other tasks,
    // meanwhile, so both take the lock, and a read gets a copy.
    private sealed class PendingWrites
    {
        private readonly Lock _lock = new();
        private readonly List<Dependency> _dependencies = [];
        private bool _probed;

        public void Add(List<Dependency> dependencies)
        {
            lock (_lock)
            {
                _dependencies.AddRange(dependencies);
            }
        }

        public List<Dependency> Snapshot()
        {
            lock (_lock)
            {
                return [.. _dependencies];
            }
        }

        // Runs addProbe until one call reports it kept the probe: a write on a flow without a box keeps nothing, and
        // a later one on a flow with a box still adds it.
        public void ProbeOnce(Func<bool> addProbe)
        {
            lock (_lock)
            {
                _probed = _probed || addProbe();
            }
        }
    }
}
