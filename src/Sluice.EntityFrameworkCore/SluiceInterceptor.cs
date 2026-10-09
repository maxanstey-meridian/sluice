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

    // What saves inside an enlisted or ambient transaction wrote, published when it completes, whatever the outcome.
    // Contexts in one TransactionScope see the same Transaction, so they publish one batch. Locked, because contexts
    // on other threads can save into one transaction, and it can complete on another thread.
    private readonly ConditionalWeakTable<Transaction, List<Dependency>> _completing = new();
    private readonly Lock _completingLock = new();

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
        Capture(eventData);
        return result;
    }

    public ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    )
    {
        Capture(eventData);
        return new(result);
    }

    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Publish(Complete(eventData, keep: false));
        return result;
    }

    // The token is ignored throughout: a request cancelled after its write committed must still invalidate.
    public async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default
    )
    {
        await PublishAsync(Complete(eventData, keep: false)).ConfigureAwait(false);
        return result;
    }

    // A failed or cancelled save may have committed earlier batches (without a transaction, or under
    // AutoTransactionBehavior.Never), so it publishes too.
    public void SaveChangesFailed(DbContextErrorEventData eventData) =>
        Publish(Complete(eventData, keep: false));

    public Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default
    ) => PublishAsync(Complete(eventData, keep: false));

    public void SaveChangesCanceled(DbContextEventData eventData) =>
        Publish(Complete(eventData, keep: false));

    public Task SaveChangesCanceledAsync(
        DbContextEventData eventData,
        CancellationToken cancellationToken = default
    ) => PublishAsync(Complete(eventData, keep: false));

    // EF reports a concurrency failure here and not through SaveChangesFailed. The save is kept: if an interceptor
    // suppresses the exception, the save goes on and publishes again when it completes.
    public InterceptionResult ThrowingConcurrencyException(
        ConcurrencyExceptionEventData eventData,
        InterceptionResult result
    )
    {
        Publish(Complete(eventData, keep: true));
        return result;
    }

    public async ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
        ConcurrencyExceptionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default
    )
    {
        await PublishAsync(Complete(eventData, keep: true)).ConfigureAwait(false);
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
    ) => Publish(TakePending(transaction));

    public Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default
    ) => PublishAsync(TakePending(transaction));

    // A rollback or a failed commit publishes too: over-invalidating is safe, and a failed commit's outcome is
    // unknown.
    public void TransactionRolledBack(
        DbTransaction transaction,
        TransactionEndEventData eventData
    ) => Publish(TakePending(transaction));

    public Task TransactionRolledBackAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default
    ) => PublishAsync(TakePending(transaction));

    public void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) =>
        Publish(TakePending(transaction));

    public Task TransactionFailedAsync(
        DbTransaction transaction,
        TransactionErrorEventData eventData,
        CancellationToken cancellationToken = default
    ) => PublishAsync(TakePending(transaction));

    private void Capture(DbContextEventData eventData)
    {
        if (eventData.Context is { } context)
        {
            _saves.AddOrUpdate(context, CapturedSave.Capture(context));
        }
    }

    // The save's dependencies to publish now, or null when there are none or they wait for a transaction. Inside a
    // transaction they're derived here, before the commit (where a throw is safe), because the entries can change
    // again before it. Otherwise they're derived as they're published, where a failure is logged.
    private Func<List<Dependency>>? Complete(DbContextEventData eventData, bool keep)
    {
        if (eventData.Context is not { } context || !_saves.TryGetValue(context, out var save))
        {
            return null;
        }

        if (!keep)
        {
            _saves.Remove(context);
        }

        var transaction = ContextTransaction.Of(context);
        if (transaction.Ef is { } ef)
        {
            // A transaction's connection runs one command at a time, so its saves never add here concurrently.
            _pending.GetValue(ef, _ => []).AddRange(save.Dependencies());
            return null;
        }

        if (transaction.Ambient is { } ambient)
        {
            AddCompleting(ambient, save.Dependencies());
            return null;
        }

        return save.Dependencies;
    }

    private void AddCompleting(Transaction transaction, List<Dependency> dependencies)
    {
        lock (_completingLock)
        {
            if (_completing.TryGetValue(transaction, out var completing))
            {
                completing.AddRange(dependencies);
                return;
            }

            _completing.Add(transaction, dependencies);
        }

        // Raised synchronously, maybe on another thread, and at once if the transaction has already completed. So
        // it publishes with the sync Invalidate and never throws: a throw skips the handlers after it, and escapes
        // into whatever completed the transaction.
        transaction.TransactionCompleted += (_, _) => Publish(TakeCompleting(transaction));
    }

    private Func<List<Dependency>>? TakeCompleting(Transaction transaction)
    {
        lock (_completingLock)
        {
            if (!_completing.TryGetValue(transaction, out var completing))
            {
                return null;
            }

            _completing.Remove(transaction);
            return () => completing;
        }
    }

    private Func<List<Dependency>>? TakePending(DbTransaction transaction)
    {
        if (!_pending.TryGetValue(transaction, out var pending))
        {
            return null;
        }

        _pending.Remove(transaction);
        return () => pending;
    }

    private void Publish(Func<List<Dependency>>? dependencies)
    {
        if (dependencies is null)
        {
            return;
        }

        try
        {
            _cache.Invalidate(dependencies());
        }
        catch (Exception exception)
        {
            LogPublishFailed(exception);
        }
    }

    private async Task PublishAsync(Func<List<Dependency>>? dependencies)
    {
        if (dependencies is null)
        {
            return;
        }

        try
        {
            await _cache.InvalidateAsync(dependencies()).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogPublishFailed(exception);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Sluice couldn't invalidate the cached values that depend on a committed EF write. They stay cached until their duration ends."
    )]
    private partial void LogPublishFailed(Exception exception);
}
