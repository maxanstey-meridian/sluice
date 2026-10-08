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
    private readonly FusionCacheEntryOptions _entryOptions;
    private readonly FusionCacheEntryOptions _tagOptions;

    internal SluiceCache(IFusionCache cache, FusionCacheEntryOptions tagOptions)
    {
        _cache = cache;
        _tagOptions = tagOptions;

        // Per-call pins (DESIGN §5.4): every one of these would let a compute outlive its caller or
        // stamp its entry at completion, which reopens the invalidation race.
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
    /// <exception cref="InvalidOperationException">
    /// The compute recorded no dependencies, or this was called from inside another compute.
    /// Nothing is cached in either case.
    /// </exception>
    public async ValueTask<TValue> GetOrSetAsync<TKey, TValue>(
        Query<TKey, TValue> query,
        TKey key,
        Func<SluiceReads, CancellationToken, Task<TValue>> compute,
        CancellationToken ct = default
    )
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(compute);

        if (ActiveCompute.Current is { } outer)
        {
            // On a hit the outer compute couldn't learn the inner value's dependencies, so they'd be lost.
            var nested = new InvalidOperationException(
                $"SluiceCache.GetOrSetAsync for query '{query.Name}' was called from inside another compute. "
                    + "Read the underlying data directly in the outer compute and record its dependencies there."
            );
            outer.Violate(nested);
            throw nested;
        }

        var entryKey = query.EntryKey(key);
        var options = OptionsFor(query);
        var reads = new SluiceReads(ct);

        // The caller's token goes to the compute, not to FusionCache: on a cancelled token FusionCache throws
        // without awaiting its factory, so the caller would resume while the compute still uses its resources
        // (DESIGN §5.4, D14). Cancelling ends the compute first, and the caller resumes after it.
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
    public ValueTask InvalidateAsync(Dependency dependency, CancellationToken ct = default) =>
        InvalidateAsync([dependency], ct);

    /// <summary>Evicts every cached value that read any of <paramref name="dependencies"/>. Call it after your write commits.</summary>
    public async ValueTask InvalidateAsync(
        IEnumerable<Dependency> dependencies,
        CancellationToken ct = default
    )
    {
        var tags = Dependency.TagsOf(dependencies, dependency => dependency.InvalidationTags);
        await Task.WhenAll(tags.Select(tag => _cache.RemoveByTagAsync(tag, token: ct).AsTask()))
            .ConfigureAwait(false);
    }

    /// <summary>The synchronous twin of <see cref="InvalidateAsync(IEnumerable{Dependency}, CancellationToken)"/>.</summary>
    public void Invalidate(params IEnumerable<Dependency> dependencies)
    {
        foreach (
            var tag in Dependency.TagsOf(dependencies, dependency => dependency.InvalidationTags)
        )
        {
            _cache.RemoveByTag(tag);
        }
    }

    /// <summary>Evicts everything Sluice cached.</summary>
    public ValueTask ClearAsync(CancellationToken ct = default) =>
        _cache.ClearAsync(allowFailSafe: false, token: ct);

    private FusionCacheEntryOptions OptionsFor<TKey, TValue>(Query<TKey, TValue> query)
        where TKey : notnull
    {
        if (query.Duration is not { } duration)
        {
            return _entryOptions;
        }

        var options = _entryOptions.Duplicate(duration);
        EntryLifetime.EnsureWithin(options, _tagOptions, $"Query '{query.Name}'");
        return options;
    }

    private static async Task<TValue> ComputeAsync<TValue>(
        SluiceReads reads,
        Func<SluiceReads, CancellationToken, Task<TValue>> compute,
        FusionCacheFactoryExecutionContext<TValue> ctx,
        CancellationToken ct
    )
    {
        ActiveCompute.Current = reads;

        TValue value = default!;
        string[] tags;
        try
        {
            value = await compute(reads, ct).ConfigureAwait(false);
        }
        catch (Exception) when (reads.HasViolation)
        {
            // The guard violation is rethrown once GetOrSetAsync returns.
        }
        finally
        {
            tags = reads.Close();
        }

        if (tags.Length == 0)
        {
            reads.Violate(
                new InvalidOperationException(
                    "The compute recorded no dependencies, so nothing could ever invalidate its value. "
                        + "Record what it reads with read.From(...), or use IFusionCache directly for values with no tracked dependencies."
                )
            );
        }

        if (reads.HasViolation)
        {
            // Returning (rather than throwing) keeps FusionCache's fail-safe from serving a stale
            // value over a guard violation (DESIGN §5.3); these flags make sure nothing is stored.
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
