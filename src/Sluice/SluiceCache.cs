using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Transactions;
using Sluice.Infrastructure;
using ZiggyCreatures.Caching.Fusion;

namespace Sluice;

/// <summary>
/// The Sluice entry point: cached values that record what they read, and invalidation by dependency.
/// Registered by <see cref="SluiceServiceCollectionExtensions.AddSluice"/>.
/// </summary>
/// <remarks>
/// The service provider disposes it, which publishes at once every invalidation still waiting to be published again
/// (see <see cref="InvalidateAsync(IEnumerable{Dependency})"/>).
/// </remarks>
public sealed class SluiceCache : IAsyncDisposable, IDisposable
{
    internal const string CacheName = "sluice";

    // How long after an invalidation or clear it's published again. FusionCache 2.9 writes a tag's marker without the
    // lock its tag check holds, and the check writes what it read unconditionally, so an invalidation landing inside a
    // check on the same node is overwritten (FusionCache#659). The losing check leaves its marker in memory, so a
    // second publish sticks unless another check's distributed-cache read outlasts the delay: 5 s is StackExchange.Redis's
    // default timeout, so such a read has failed by then.
    internal static readonly TimeSpan RepublishDelay = TimeSpan.FromSeconds(5);

    private readonly IFusionCache _cache;
    private readonly IServiceProvider _services;
    private readonly FusionCacheEntryOptions _entryOptions;
    private readonly FusionCacheEntryOptions _tagOptions;
    private readonly TimeProvider _time;
    private volatile Func<Transaction, IReadOnlyCollection<Dependency>>? _ambientWrites;
    private readonly Lock _storeChecksLock = new();
    private volatile Func<object, Exception?>[] _storeChecks = [];

    // Cancelled on dispose, which ends every re-publish's wait at once. Each re-publish waiting or running, completed
    // when it has, so dispose can wait for them before FusionCache is disposed after it.
    private readonly CancellationTokenSource _disposing = new();
    private readonly HashSet<Task> _republishing = [];

    internal SluiceCache(
        IFusionCache cache,
        IServiceProvider services,
        FusionCacheEntryOptions tagOptions,
        FusionCacheInternalStrings internalStrings,
        TimeProvider time
    )
    {
        _cache = cache;
        _services = services;
        _tagOptions = tagOptions;
        _time = time;

        // Per-call pins: every one of these would let a compute outlive its caller or stamp its entry at
        // completion, which reopens the invalidation race.
        _entryOptions = cache.DefaultEntryOptions.Duplicate();
        _entryOptions.EagerRefreshThreshold = null;
        _entryOptions.AllowTimedOutFactoryBackgroundCompletion = false;
        _entryOptions.FactorySoftTimeout = Timeout.InfiniteTimeSpan;
        _entryOptions.FactoryHardTimeout = Timeout.InfiniteTimeSpan;

        EntryLifetime.EnsureWithin(
            _entryOptions,
            _tagOptions,
            "The Sluice cache's DefaultEntryOptions"
        );
        QueryNames.EnsureTagPrefix(internalStrings.TagCacheKeyPrefix);
        Dependency.EnsureNotATag(internalStrings.ClearRemoveTag, "ClearRemoveTag");
        Dependency.EnsureNotATag(internalStrings.ClearExpireTag, "ClearExpireTag");
    }

    // How a package reads an ambient transaction's pending writes. Until one registers, a call doesn't read
    // Transaction.Current for nothing.
    internal void OnAmbient(Func<Transaction, IReadOnlyCollection<Dependency>> pending) =>
        _ambientWrites = pending;

    // Copied on write, so a compute reads the checks without a lock. Packages add theirs once, at registration.
    internal void AddStoreCheck(Func<object, Exception?> check)
    {
        lock (_storeChecksLock)
        {
            if (!_storeChecks.Contains(check))
            {
                _storeChecks = [.. _storeChecks, check];
            }
        }
    }

    /// <summary>
    /// Returns the cached value for <paramref name="key"/>, or runs <paramref name="compute"/> and caches
    /// its result, tagged with every dependency it recorded through <c>read.From</c>.
    /// </summary>
    /// <param name="query">The query whose entry to read.</param>
    /// <param name="key">The query key.</param>
    /// <param name="compute">
    /// Computes the value on a miss, or on every call while the caller's own transaction has uncommitted writes (see
    /// <see cref="SluiceFlow"/>), recording what it reads through <c>read.From</c>. It runs outside any ambient
    /// transaction, so it reads what's committed.
    /// </param>
    /// <param name="ct">
    /// Passed to <paramref name="compute"/>, so cancelling cancels the compute; the call returns once the compute
    /// has ended. Waiting on another caller's compute for the same key can't be cancelled.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The compute recorded no dependencies (and didn't call <c>read.DoNotCache()</c>), used another compute's
    /// <c>read</c>, or depends on writes the caller's own transaction hasn't committed (see <see cref="SluiceFlow"/>),
    /// or this was called from inside another compute, a fused <c>read.From(dependency, fetch)</c> fetch included.
    /// Nothing is cached in any case.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The compute was cancelled (unless disposing what it held also threw), or <paramref name="ct"/> was already
    /// cancelled, in which case no cached value is returned and nothing is computed.
    /// </exception>
    /// <exception cref="AggregateException">
    /// The compute threw (or was cancelled) and disposing what it held (such as <c>read.Db</c>'s context) threw too,
    /// or several disposes threw. A single failed dispose after a compute that didn't throw reaches the caller as it
    /// is. Nothing is cached in any case.
    /// </exception>
    [SuppressMessage(
        "ApiDesign",
        "RS0026:Do not add multiple public overloads with optional parameters",
        Justification = "The overloads differ in their query type, so a call can't bind to the wrong one."
    )]
    public ValueTask<TValue> GetOrSetAsync<TKey, TValue>(
        Query<TKey, TValue> query,
        TKey key,
        Func<SluiceReads, CancellationToken, Task<TValue>> compute,
        CancellationToken ct = default
    )
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(compute);
        ThrowIfNested(query.Name);
        return GetOrSetAsync(
            query.Name,
            query.EntryKey(key),
            query.Duration,
            query.Size,
            compute,
            ct
        );
    }

    /// <summary>
    /// Returns the cached value of a query with no key, or runs <paramref name="compute"/> and caches its result,
    /// tagged with every dependency it recorded through <c>read.From</c>.
    /// </summary>
    /// <param name="query">The query whose entry to read.</param>
    /// <param name="compute">
    /// Computes the value on a miss, or on every call while the caller's own transaction has uncommitted writes (see
    /// <see cref="SluiceFlow"/>), recording what it reads through <c>read.From</c>. It runs outside any ambient
    /// transaction, so it reads what's committed.
    /// </param>
    /// <param name="ct">
    /// Passed to <paramref name="compute"/>, so cancelling cancels the compute; the call returns once the compute
    /// has ended. Waiting on another caller's compute can't be cancelled.
    /// </param>
    /// <inheritdoc cref="GetOrSetAsync{TKey, TValue}(Query{TKey, TValue}, TKey, Func{SluiceReads, CancellationToken, Task{TValue}}, CancellationToken)" path="/exception"/>
    [SuppressMessage(
        "ApiDesign",
        "RS0026:Do not add multiple public overloads with optional parameters",
        Justification = "The overloads differ in their query type, so a call can't bind to the wrong one."
    )]
    public ValueTask<TValue> GetOrSetAsync<TValue>(
        Query<TValue> query,
        Func<SluiceReads, CancellationToken, Task<TValue>> compute,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(compute);
        ThrowIfNested(query.Name);
        return GetOrSetAsync(query.Name, query.Name, query.Duration, query.Size, compute, ct);
    }

    private async ValueTask<TValue> GetOrSetAsync<TValue>(
        string name,
        string entryKey,
        TimeSpan? duration,
        long? size,
        Func<SluiceReads, CancellationToken, Task<TValue>> compute,
        CancellationToken ct
    )
    {
        // An already-cancelled caller gets neither a hit nor a compute.
        ct.ThrowIfCancellationRequested();

        var options = OptionsFor(name, duration, size);
        var reads = new SluiceReads(name, _services, ct);

        // A value cached or computed outside the caller's transaction lacks its uncommitted writes, and a hit can't
        // be checked against them: FusionCache doesn't return an entry's tags. So the call computes fresh and leaves
        // FusionCache alone, stampede lock included, so no other caller waits on it or inherits its violation.
        if (PendingWrites() is { } pending)
        {
            return await BypassAsync(reads, compute, pending, ct).ConfigureAwait(false);
        }

        // The caller's token goes to the compute, not to FusionCache: on a cancelled token FusionCache throws
        // without awaiting its factory, so the caller would resume while the compute still uses its resources.
        // Cancelling ends the compute first, and the caller resumes after it.
        var storeChecks = _storeChecks;
        var value = await _cache
            .GetOrSetAsync<TValue>(
                entryKey,
                (ctx, _) => FactoryAsync(reads, compute, storeChecks, ctx, ct),
                options: options
            )
            .ConfigureAwait(false);

        reads.ThrowIfViolated();
        return value;
    }

    // What the caller's own transactions have written for this cache and not committed: from the flow's probes, and
    // from the caller's ambient transaction, read here, before the compute suppresses it. Null when nothing is; without
    // a box or a registered ambient lookup, that costs one AsyncLocal read.
    private List<Dependency>? PendingWrites()
    {
        var pending = SluiceFlow.Pending(this);
        if (
            _ambientWrites is { } ambientWrites
            && Transaction.Current is { } transaction
            && ambientWrites(transaction) is { Count: > 0 } ambient
        )
        {
            (pending ??= []).AddRange(ambient);
        }

        return pending;
    }

    // The compute runs as on a miss, guards and all, but nothing is stored. Its value goes to the caller unless it
    // read what the pending writes invalidate: it read only what's committed, so it would hide them.
    private static async ValueTask<TValue> BypassAsync<TValue>(
        SluiceReads reads,
        Func<SluiceReads, CancellationToken, Task<TValue>> compute,
        List<Dependency> pending,
        CancellationToken ct
    )
    {
        var (value, tags, _) = await ComputeAsync(
                reads,
                compute,
                checkedAgainstWrites: true,
                storeChecks: [],
                ct
            )
            .ConfigureAwait(false);

        var read = tags.ToHashSet(StringComparer.Ordinal);
        var overlapping = pending
            .Where(dependency => dependency.InvalidationTags.Any(read.Contains))
            .Select(dependency => dependency.Text)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (overlapping.Count > 0)
        {
            reads.Violate(
                new InvalidOperationException(
                    $"Query '{reads.Query}' depends on {string.Join(", ", overlapping)}, which the caller's own "
                        + "transaction has written and not yet committed. The compute reads only what's committed, so "
                        + "the caller would get the data without its own writes. Read that data inside the "
                        + "transaction, not through the cache, or after it commits."
                )
            );
        }

        reads.ThrowIfViolated();
        return value;
    }

    /// <summary>Evicts every cached value that read <paramref name="dependency"/>. Call it after your write commits.</summary>
    /// <param name="dependency">What was written.</param>
    /// <inheritdoc cref="InvalidateAsync(IEnumerable{Dependency})" path="/remarks"/>
    public ValueTask InvalidateAsync(Dependency dependency)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        return InvalidateAsync([dependency]);
    }

    /// <summary>Evicts every cached value that read any of <paramref name="dependencies"/>. Call it after your write commits.</summary>
    /// <param name="dependencies">What was written.</param>
    /// <remarks>
    /// Takes no cancellation token: a request aborted after its write committed must still invalidate. A distributed
    /// cache write that fails doesn't make this throw: FusionCache writes the tag again once the cache is back
    /// (auto-recovery). A slow one makes this wait, since FusionCache puts no timeout on writes, so bound it with the
    /// Redis client's own timeout and FusionCache's circuit breakers. Once it returns, the invalidation is published
    /// again 5 s later in the background, since FusionCache 2.9 can lose one that lands while the same server checks
    /// those dependencies; a value cached in between is evicted again, and disposing the cache publishes it at once.
    /// </remarks>
    public async ValueTask InvalidateAsync(IEnumerable<Dependency> dependencies)
    {
        var tags = Dependency.TagsOf(dependencies, dependency => dependency.InvalidationTags);
        if (tags.Count == 0)
        {
            return;
        }

        await RemoveByTagsAsync(tags).ConfigureAwait(false);
        PublishAgainLater(() => RemoveByTagsAsync(tags));
    }

    /// <summary>The synchronous twin of <see cref="InvalidateAsync(IEnumerable{Dependency})"/>.</summary>
    /// <param name="dependencies">What was written.</param>
    /// <inheritdoc cref="InvalidateAsync(IEnumerable{Dependency})" path="/remarks"/>
    public void Invalidate(params IEnumerable<Dependency> dependencies)
    {
        var tags = Dependency.TagsOf(dependencies, dependency => dependency.InvalidationTags);
        if (tags.Count == 0)
        {
            return;
        }

        _cache.RemoveByTag(tags);
        PublishAgainLater(() => RemoveByTagsAsync(tags));
    }

    /// <summary>Evicts everything Sluice cached.</summary>
    /// <param name="ct">Cancels the clear.</param>
    /// <remarks>
    /// Once it returns, the clear is published again 5 s later in the background, as an invalidation is (see
    /// <see cref="InvalidateAsync(IEnumerable{Dependency})"/>), so a value cached in between is evicted too.
    /// </remarks>
    public async ValueTask ClearAsync(CancellationToken ct = default)
    {
        await _cache.ClearAsync(allowFailSafe: false, token: ct).ConfigureAwait(false);
        PublishAgainLater(() => _cache.ClearAsync(allowFailSafe: false).AsTask());
    }

    /// <summary>
    /// Publishes at once every invalidation and clear still waiting to be published again, and waits for them. Never
    /// throws: FusionCache logs a failed distributed cache or backplane write.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _disposing.CancelAsync().ConfigureAwait(false);
        Task[] republishing;
        lock (_republishing)
        {
            republishing = [.. _republishing];
        }

        await Task.WhenAll(republishing).ConfigureAwait(false);
    }

    /// <summary>
    /// The synchronous twin of <see cref="DisposeAsync"/>, for a service provider disposed synchronously, which
    /// refuses a service that can only be disposed asynchronously.
    /// </summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    // FusionCache's multi-tag overload removes one tag at a time, so run them side by side.
    private Task RemoveByTagsAsync(List<string> tags) =>
        Task.WhenAll(
            tags.Select(tag => _cache.RemoveByTagAsync(tag, token: CancellationToken.None).AsTask())
        );

    // Publishes again after the delay, or at once when the cache is disposed (or already is). Nothing awaits it but
    // dispose, so a failure ends here: FusionCache has logged it.
    private void PublishAgainLater(Func<Task> publish)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_republishing)
        {
            _republishing.Add(done.Task);
        }

        // It outlives the call, so it mustn't hold the caller's async state (its request, transaction or trace) for
        // the delay. Started on this thread, so its timer exists when the call returns.
        if (ExecutionContext.IsFlowSuppressed())
        {
            _ = PublishAgainAsync(publish, done);
            return;
        }

        using (ExecutionContext.SuppressFlow())
        {
            _ = PublishAgainAsync(publish, done);
        }
    }

    private async Task PublishAgainAsync(Func<Task> publish, TaskCompletionSource done)
    {
        try
        {
            try
            {
                await Task.Delay(RepublishDelay, _time, _disposing.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Disposing: publish now.
            }

            await publish().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // FusionCache was disposed first, at shutdown.
        }
        finally
        {
            lock (_republishing)
            {
                _republishing.Remove(done.Task);
            }

            done.SetResult();
        }
    }

    private FusionCacheEntryOptions OptionsFor(string name, TimeSpan? duration, long? size)
    {
        if (duration is null && size is null)
        {
            return _entryOptions;
        }

        var options = _entryOptions.Duplicate(duration);
        options.Size = size ?? options.Size;

        if (duration is not null)
        {
            EntryLifetime.EnsureWithin(options, _tagOptions, $"Query '{name}'");
        }

        return options;
    }

    // On a hit the outer compute couldn't learn the inner value's dependencies, so they'd be lost. That holds inside a
    // fused fetch too, so this ignores whether the call is in one.
    private static void ThrowIfNested(string queryName)
    {
        if (ActiveCompute.Reads is { } outer)
        {
            throw ActiveCompute.Violate(
                outer,
                new InvalidOperationException(
                    $"SluiceCache.GetOrSetAsync for query '{queryName}' was called from inside another compute. "
                        + "Read the underlying data directly in the outer compute and record its dependencies there."
                )
            );
        }
    }

    // FusionCache's factory: stores the value with the tags its compute recorded, unless it asked not to be cached or
    // a guard was violated.
    private static async Task<TValue> FactoryAsync<TValue>(
        SluiceReads reads,
        Func<SluiceReads, CancellationToken, Task<TValue>> compute,
        Func<object, Exception?>[] storeChecks,
        FusionCacheFactoryExecutionContext<TValue> ctx,
        CancellationToken ct
    )
    {
        var (value, tags, uncached) = await ComputeAsync(
                reads,
                compute,
                checkedAgainstWrites: false,
                storeChecks,
                ct
            )
            .ConfigureAwait(false);

        var violated = reads.HasViolation;
        if (violated || uncached)
        {
            // Returning (rather than throwing) keeps FusionCache's fail-safe from serving a stale
            // value over a guard violation; these flags make sure nothing is stored. A violation is
            // rethrown once GetOrSetAsync returns; an uncached value goes to the caller.
            var options = ctx.Options;
            options.SkipMemoryCacheWrite = true;
            options.SkipDistributedCacheWrite = true;
            options.SkipBackplaneNotifications = true;
            return violated ? default! : value;
        }

        ctx.Tags = tags;
        return value;
    }

    // Runs the compute, whether its value is to be stored or not: as the flow's compute, outside the caller's ambient
    // transaction, then the steps it added to run before the store, then closes it and disposes what it held. Returns
    // the value, the tags it recorded and whether it asked not to be cached; a guard violation is left on reads, for
    // the caller to check. The compute's own exception is rethrown. When its tags are checked against the flow's
    // uncommitted writes, the steps run even if it asked not to be cached: what they record is checked too. The store
    // checks run last, on a value that will be stored.
    private static async Task<(TValue Value, string[] Tags, bool Uncached)> ComputeAsync<TValue>(
        SluiceReads reads,
        Func<SluiceReads, CancellationToken, Task<TValue>> compute,
        bool checkedAgainstWrites,
        Func<object, Exception?>[] storeChecks,
        CancellationToken ct
    )
    {
        ActiveCompute.Reads = reads;

        TValue value = default!;
        Exception? computeFailure = null;
        string[] tags;
        bool uncached;
        List<Exception>? releaseFailures;

        // A cached value is served to every caller, so it mustn't see a transaction's uncommitted rows. Connections the
        // compute opens don't enlist in the caller's ambient transaction.
        using (
            Transaction.Current is null
                ? null
                : new TransactionScope(
                    TransactionScopeOption.Suppress,
                    TransactionScopeAsyncFlowOption.Enabled
                )
        )
        {
            try
            {
                value = await compute(reads, ct).ConfigureAwait(false);
                await reads.RunBeforeStoreAsync(checkedAgainstWrites).ConfigureAwait(false);

                // On the compute's flow, before what it holds is disposed. A value that won't be stored isn't
                // served to anyone else, so it isn't checked.
                if (storeChecks.Length > 0 && value is not null && reads.WillStore)
                {
                    foreach (var check in storeChecks)
                    {
                        if (check(value) is { } refusal)
                        {
                            reads.Violate(refusal);
                            break;
                        }
                    }
                }
            }
            catch (Exception failure)
            {
                computeFailure = failure;
            }

            (tags, uncached) = reads.Close();
            releaseFailures = await reads.ReleaseAsync().ConfigureAwait(false);
        }

        // A failed dispose mustn't throw out of the factory: it would replace the compute's exception or a guard
        // violation, skip the check below, and with fail-safe on let FusionCache serve an expired value over it. It
        // becomes a violation instead, behind any guard violation the compute recorded (Violate keeps the first).
        if (releaseFailures is not null)
        {
            reads.Violate(
                (computeFailure, releaseFailures) switch
                {
                    (null, [var only]) => only,
                    (null, _) => new AggregateException(
                        "Disposing what the compute held threw more than once.",
                        releaseFailures
                    ),
                    _ => new AggregateException(
                        "The compute threw, and disposing what it held threw too.",
                        [computeFailure, .. releaseFailures]
                    ),
                }
            );
        }
        else if (computeFailure is not null && !reads.HasViolation)
        {
            // The compute's own exception, as it would have propagated; a guard violation is rethrown instead once
            // GetOrSetAsync returns.
            ExceptionDispatchInfo.Throw(computeFailure);
        }

        // A value that isn't stored needs nothing to invalidate it.
        if (tags.Length == 0 && !uncached)
        {
            reads.Violate(
                new InvalidOperationException(
                    "The compute recorded no dependencies, so nothing could ever invalidate its value. Record what it "
                        + "reads with read.From(...); with EF Core, query a context from read.Db<TContext>(), "
                        + "configured with UseSluice. For a value with nothing to depend on, cache it in a FusionCache of "
                        + "your own: register one with services.AddFusionCache() and inject IFusionCache (Sluice's cache "
                        + "is a named one, not registered as IFusionCache)."
                )
            );
        }

        return (value, tags, uncached);
    }
}
