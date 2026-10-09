using System.Data.Common;
using System.Runtime.CompilerServices;
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
        Capture(eventData.Context);
        return result;
    }

    public ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    )
    {
        Capture(eventData.Context);
        return new(result);
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
    // pending on it belongs to an earlier transaction that was disposed without committing.
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
    ) => PublishPending(transaction, keep: false);

    public Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default
    ) => PublishPendingAsync(transaction, keep: false);

    // A rollback publishes too: over-invalidating is safe.
    public void TransactionRolledBack(
        DbTransaction transaction,
        TransactionEndEventData eventData
    ) => PublishPending(transaction, keep: false);

    public Task TransactionRolledBackAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default
    ) => PublishPendingAsync(transaction, keep: false);

    // A failed commit's outcome is unknown, so it publishes. EF also reports a savepoint that failed to release
    // here and carries on, and the transaction may still commit more after it, so the pending writes are kept for
    // its commit or rollback to publish again.
    public void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) =>
        PublishPending(transaction, keep: true);

    public Task TransactionFailedAsync(
        DbTransaction transaction,
        TransactionErrorEventData eventData,
        CancellationToken cancellationToken = default
    ) => PublishPendingAsync(transaction, keep: true);

    // Refuses a provider it can't see transactions for here, before anything is written: afterwards, a throw would
    // make the caller retry a write that committed.
    private void Capture(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        ContextTransaction.EnsureRelational(context);
        _saves.TryGetValue(context, out var unfinished);
        _saves.AddOrUpdate(context, CapturedSave.Capture(context, unfinished));
    }

    private void Complete(DbContext? context, bool keep)
    {
        if (
            context is not null
            && Take(context, keep) is { } save
            && !WaitsForTransaction(context, save)
        )
        {
            Publish(save.Dependencies);
        }
    }

    private async Task CompleteAsync(DbContext? context, bool keep)
    {
        if (
            context is not null
            && Take(context, keep) is { } save
            && !WaitsForTransaction(context, save)
        )
        {
            await PublishAsync(save.Dependencies).ConfigureAwait(false);
        }
    }

    private CapturedSave? Take(DbContext context, bool keep)
    {
        if (!_saves.TryGetValue(context, out var save))
        {
            return null;
        }

        if (!keep)
        {
            _saves.Remove(context);
        }

        return save;
    }

    // Hands the save to the transaction it ran in, if any, to publish when that ends. Its dependencies are derived
    // now, before the commit, where a throw is still safe, because the entries can change again before it.
    private bool WaitsForTransaction(DbContext context, CapturedSave save)
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
            var dependencies = save.Dependencies();

            // Raised synchronously, maybe on another thread, and at once if the transaction has already completed.
            // So it publishes with the sync Invalidate and never throws: a throw skips the handlers after it, and
            // escapes into whatever completed the transaction.
            ambient.TransactionCompleted += (_, _) => Publish(() => dependencies);
            return true;
        }

        return false;
    }

    private void PublishPending(DbTransaction transaction, bool keep)
    {
        if (TakePending(transaction, keep) is { } pending)
        {
            Publish(() => pending);
        }
    }

    private async Task PublishPendingAsync(DbTransaction transaction, bool keep)
    {
        if (TakePending(transaction, keep) is { } pending)
        {
            await PublishAsync(() => pending).ConfigureAwait(false);
        }
    }

    private List<Dependency>? TakePending(DbTransaction transaction, bool keep)
    {
        if (!_pending.TryGetValue(transaction, out var pending))
        {
            return null;
        }

        if (!keep)
        {
            _pending.Remove(transaction);
        }

        return pending;
    }

    // Takes the dependencies as a function so that deriving them, after a commit without a transaction, fails
    // into the log too.
    private void Publish(Func<List<Dependency>> derive)
    {
        List<Dependency>? dependencies = null;
        try
        {
            dependencies = derive();
            _cache.Invalidate(dependencies);
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
            await _cache.InvalidateAsync(dependencies).ConfigureAwait(false);
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
        Message = "Sluice couldn't invalidate the cached values that depend on an EF write once it ended, whether it committed, rolled back or failed. They stay cached until their duration ends. Dependencies to invalidate: {DependencyCount}."
    )]
    private partial void LogPublishFailed(Exception exception, int? dependencyCount);
}
