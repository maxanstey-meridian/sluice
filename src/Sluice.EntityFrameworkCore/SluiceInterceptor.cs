using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Sluice.EntityFrameworkCore;

/// <summary>
/// Invalidates what each tracked save wrote, once it has committed. One instance per <see cref="SluiceCache"/>,
/// shared by every context that uses it, so contexts sharing one transaction share its pending invalidations.
/// </summary>
/// <remarks>
/// Never drops: every outcome that might have committed something publishes, because over-invalidating is safe.
/// Publishing after a commit logs failures instead of throwing, since a throw would make the caller retry a write
/// that already committed. Sync EF calls publish synchronously.
/// </remarks>
internal sealed partial class SluiceInterceptor : ISaveChangesInterceptor, IDbTransactionInterceptor
{
    private static readonly ConditionalWeakTable<SluiceCache, SluiceInterceptor> _instances = new();

    private readonly SluiceCache _cache;
    private readonly ILogger _logger;

    // A save in progress, from SavingChanges until EF reports how it ended. Weak, so a context that's dropped
    // mid-save leaves nothing behind.
    private readonly ConditionalWeakTable<DbContext, CapturedSave> _saves = new();

    // What saves inside an EF transaction wrote, published when the transaction ends. Keyed by the DbTransaction,
    // which contexts sharing a transaction have in common. Weak, because a transaction disposed without commit or
    // rollback raises no event, and committed nothing.
    private readonly ConditionalWeakTable<DbTransaction, List<Dependency>> _pending = new();

    private SluiceInterceptor(SluiceCache cache, ILogger<SluiceInterceptor> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    /// <summary>The interceptor for the <see cref="SluiceCache"/> registered in <paramref name="services"/>.</summary>
    internal static SluiceInterceptor For(IServiceProvider services) =>
        _instances.GetValue(
            services.GetRequiredService<SluiceCache>(),
            cache => new(cache, services.GetRequiredService<ILogger<SluiceInterceptor>>())
        );

    public InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result
    )
    {
        if (eventData.Context is { } context)
        {
            PublishLeftover(context);
            _saves.AddOrUpdate(context, CapturedSave.Capture(context));
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
            _saves.AddOrUpdate(context, CapturedSave.Capture(context));
        }

        return result;
    }

    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Complete(eventData.Context, keep: false);
        return result;
    }

    // The token is ignored throughout: a request cancelled after its write committed must still invalidate.
    public async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default
    )
    {
        await CompleteAsync(eventData.Context, keep: false).ConfigureAwait(false);
        return result;
    }

    // A failed or cancelled save may have committed earlier batches (without a transaction, or under
    // AutoTransactionBehavior.Never), so it publishes too.
    public void SaveChangesFailed(DbContextErrorEventData eventData) =>
        Complete(eventData.Context, keep: false);

    public Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default
    ) => CompleteAsync(eventData.Context, keep: false);

    public void SaveChangesCanceled(DbContextEventData eventData) =>
        Complete(eventData.Context, keep: false);

    public Task SaveChangesCanceledAsync(
        DbContextEventData eventData,
        CancellationToken cancellationToken = default
    ) => CompleteAsync(eventData.Context, keep: false);

    // EF reports a concurrency failure here and not through SaveChangesFailed. The save is kept: if an interceptor
    // suppresses the exception, the save goes on and publishes again when it completes.
    public InterceptionResult ThrowingConcurrencyException(
        ConcurrencyExceptionEventData eventData,
        InterceptionResult result
    )
    {
        Complete(eventData.Context, keep: true);
        return result;
    }

    public async ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
        ConcurrencyExceptionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default
    )
    {
        await CompleteAsync(eventData.Context, keep: true).ConfigureAwait(false);
        return result;
    }

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
        return new(result);
    }

    public void TransactionCommitted(
        DbTransaction transaction,
        TransactionEndEventData eventData
    ) => Publish(() => Take(_pending, transaction, keep: false) ?? []);

    public Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default
    ) => PublishAsync(() => Take(_pending, transaction, keep: false) ?? []);

    // A rollback publishes too: over-invalidating is safe.
    public void TransactionRolledBack(
        DbTransaction transaction,
        TransactionEndEventData eventData
    ) => Publish(() => Take(_pending, transaction, keep: false) ?? []);

    public Task TransactionRolledBackAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default
    ) => PublishAsync(() => Take(_pending, transaction, keep: false) ?? []);

    // A failed commit's outcome is unknown, so it publishes. EF also reports a savepoint that failed to release
    // here and carries on, and the transaction may still commit more after it, so the pending writes are kept for
    // its commit or rollback to publish again.
    public void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) =>
        Publish(() => Take(_pending, transaction, keep: true) ?? []);

    public Task TransactionFailedAsync(
        DbTransaction transaction,
        TransactionErrorEventData eventData,
        CancellationToken cancellationToken = default
    ) => PublishAsync(() => Take(_pending, transaction, keep: true) ?? []);

    private void Complete(DbContext? context, bool keep)
    {
        if (context is not null && Take(_saves, context, keep) is { } save)
        {
            if (!DeferToTransaction(context, save))
            {
                Publish(save.Dependencies);
            }
        }
    }

    private async Task CompleteAsync(DbContext? context, bool keep)
    {
        if (context is not null && Take(_saves, context, keep) is { } save)
        {
            if (!DeferToTransaction(context, save))
            {
                await PublishAsync(save.Dependencies).ConfigureAwait(false);
            }
        }
    }

    // Hands the save to the transaction the context is in now, if any, to publish when that ends: read here, not
    // when the save began, since an interceptor may have committed the save's transaction already. Its dependencies
    // are derived now, before the commit, where a throw is still safe, because the entries can change again before it.
    private bool DeferToTransaction(DbContext context, CapturedSave save)
    {
        var transaction = ContextTransaction.Of(context);
        if (transaction.Ef is { } ef)
        {
            // A transaction's connection runs one command at a time, so its saves never add here concurrently.
            _pending.GetValue(ef, _ => []).AddRange(save.Dependencies());
            return true;
        }

        if (transaction.Ambient is { } ambient)
        {
            PublishWhenCompleted(ambient, save.Dependencies());
            return true;
        }

        return false;
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
            _pending.GetValue(ef, _ => []).AddRange(dependencies);
        }
        else if (leftover.Transaction.Ambient is { } ambient)
        {
            try
            {
                PublishWhenCompleted(ambient, dependencies);
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
        Message = "Sluice couldn't invalidate the cached values that depend on an EF save; they stay cached until their duration ends. Dependencies: {DependencyCount}"
    )]
    private partial void LogPublishFailed(Exception exception, int? dependencyCount);
}
