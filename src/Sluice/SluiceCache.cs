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
public sealed class SluiceCache
{
    internal const string CacheName = "sluice";

    private readonly IFusionCache _cache;
    private readonly IServiceProvider _services;
    private readonly FusionCacheEntryOptions _entryOptions;
    private readonly FusionCacheEntryOptions _tagOptions;

    internal SluiceCache(
        IFusionCache cache,
        IServiceProvider services,
        FusionCacheEntryOptions tagOptions
    )
    {
        _cache = cache;
        _services = services;
        _tagOptions = tagOptions;

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
    }

    /// <summary>
    /// Returns the cached value for <paramref name="key"/>, or runs <paramref name="compute"/> and caches
    /// its result, tagged with every dependency it recorded through <c>read.From</c>.
    /// </summary>
    /// <param name="query">The query whose entry to read.</param>
    /// <param name="key">The query key.</param>
    /// <param name="compute">
    /// Computes the value on a miss, recording what it reads through <c>read.From</c>. It runs outside any ambient
    /// transaction, so it reads what's committed.
    /// </param>
    /// <param name="ct">
    /// Passed to <paramref name="compute"/>, so cancelling cancels the compute; the call returns once the compute
    /// has ended. Waiting on another caller's compute for the same key can't be cancelled.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The compute recorded no dependencies, or this was called from inside another compute, a fused
    /// <c>read.From(dependency, fetch)</c> fetch included. Nothing is cached in either case.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The compute was cancelled, or <paramref name="ct"/> was already cancelled, in which case no cached value is
    /// returned and nothing is computed.
    /// </exception>
    /// <exception cref="AggregateException">
    /// The compute threw and disposing what it held (such as <c>read.Db</c>'s context) threw too, or several disposes
    /// threw. A single failed dispose after a compute that didn't throw reaches the caller as it is. Nothing is cached
    /// in either case.
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
        return GetOrSetAsync(query.Name, query.EntryKey(key), query.Duration, compute, ct);
    }

    /// <summary>
    /// Returns the cached value of a query with no key, or runs <paramref name="compute"/> and caches its result,
    /// tagged with every dependency it recorded through <c>read.From</c>.
    /// </summary>
    /// <param name="query">The query whose entry to read.</param>
    /// <param name="compute">
    /// Computes the value on a miss, recording what it reads through <c>read.From</c>. It runs outside any ambient
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
        return GetOrSetAsync(query.Name, query.Name, query.Duration, compute, ct);
    }

    private async ValueTask<TValue> GetOrSetAsync<TValue>(
        string name,
        string entryKey,
        TimeSpan? duration,
        Func<SluiceReads, CancellationToken, Task<TValue>> compute,
        CancellationToken ct
    )
    {
        // An already-cancelled caller gets neither a hit nor a compute.
        ct.ThrowIfCancellationRequested();

        var options = OptionsFor(name, duration);
        var reads = new SluiceReads(name, _services, ct);

        // The caller's token goes to the compute, not to FusionCache: on a cancelled token FusionCache throws
        // without awaiting its factory, so the caller would resume while the compute still uses its resources.
        // Cancelling ends the compute first, and the caller resumes after it.
        var value = await _cache
            .GetOrSetAsync<TValue>(
                entryKey,
                (ctx, _) => ComputeAsync(reads, compute, ctx, ct),
                options: options
            )
            .ConfigureAwait(false);

        reads.ThrowIfViolated();
        return value;
    }

    /// <summary>Evicts every cached value that read <paramref name="dependency"/>. Call it after your write commits.</summary>
    /// <param name="dependency">What was written.</param>
    /// <remarks>
    /// Takes no cancellation token: a request aborted after its write committed must still invalidate. A distributed
    /// cache write that fails doesn't make this throw: FusionCache writes the tag again once the cache is back
    /// (auto-recovery). A slow one makes this wait, since FusionCache puts no timeout on writes, so bound it with the
    /// Redis client's own timeout and FusionCache's circuit breakers.
    /// </remarks>
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
    /// Redis client's own timeout and FusionCache's circuit breakers.
    /// </remarks>
    public async ValueTask InvalidateAsync(IEnumerable<Dependency> dependencies)
    {
        var tags = Dependency.TagsOf(dependencies, dependency => dependency.InvalidationTags);

        // FusionCache's multi-tag overload removes one tag at a time, so run them side by side.
        await Task.WhenAll(
                tags.Select(tag =>
                    _cache.RemoveByTagAsync(tag, token: CancellationToken.None).AsTask()
                )
            )
            .ConfigureAwait(false);
    }

    /// <summary>The synchronous twin of <see cref="InvalidateAsync(IEnumerable{Dependency})"/>.</summary>
    /// <param name="dependencies">What was written.</param>
    public void Invalidate(params IEnumerable<Dependency> dependencies) =>
        _cache.RemoveByTag(
            Dependency.TagsOf(dependencies, dependency => dependency.InvalidationTags)
        );

    /// <summary>Evicts everything Sluice cached.</summary>
    /// <param name="ct">Cancels the clear.</param>
    public ValueTask ClearAsync(CancellationToken ct = default) =>
        _cache.ClearAsync(allowFailSafe: false, token: ct);

    private FusionCacheEntryOptions OptionsFor(string name, TimeSpan? duration)
    {
        if (duration is not { } value)
        {
            return _entryOptions;
        }

        var options = _entryOptions.Duplicate(value);
        EntryLifetime.EnsureWithin(options, _tagOptions, $"Query '{name}'");
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

    private static async Task<TValue> ComputeAsync<TValue>(
        SluiceReads reads,
        Func<SluiceReads, CancellationToken, Task<TValue>> compute,
        FusionCacheFactoryExecutionContext<TValue> ctx,
        CancellationToken ct
    )
    {
        ActiveCompute.Reads = reads;

        TValue value = default!;
        Exception? computeFailure = null;
        string[] tags;
        Exception? releaseFailure;

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
            }
            catch (Exception failure)
            {
                computeFailure = failure;
            }

            tags = reads.Close();
            releaseFailure = await reads.ReleaseAsync().ConfigureAwait(false);
        }

        // A failed dispose mustn't throw out of the factory: it would replace the compute's exception or a guard
        // violation, skip the check below, and with fail-safe on let FusionCache serve an expired value over it. It
        // becomes a violation instead, behind any guard violation the compute recorded (Violate keeps the first).
        if (releaseFailure is not null)
        {
            reads.Violate(
                computeFailure is null
                    ? releaseFailure
                    : new AggregateException(computeFailure, releaseFailure)
            );
        }
        else if (computeFailure is not null && !reads.HasViolation)
        {
            // The compute's own exception, as it would have propagated; a guard violation is rethrown instead once
            // GetOrSetAsync returns.
            ExceptionDispatchInfo.Throw(computeFailure);
        }

        if (tags.Length == 0)
        {
            reads.Violate(
                new InvalidOperationException(
                    "The compute recorded no dependencies, so nothing could ever invalidate its value. Record what it "
                        + "reads with read.From(...); with EF Core, query a context from read.Db<TContext>(), "
                        + "configured with UseSluice. For values with no tracked dependencies, use IFusionCache directly."
                )
            );
        }

        if (reads.HasViolation)
        {
            // Returning (rather than throwing) keeps FusionCache's fail-safe from serving a stale
            // value over a guard violation; these flags make sure nothing is stored.
            var options = ctx.Options;
            options.SkipMemoryCacheWrite = true;
            options.SkipDistributedCacheWrite = true;
            options.SkipBackplaneNotifications = true;
            return default!;
        }

        ctx.Tags = tags;
        return value;
    }
}
