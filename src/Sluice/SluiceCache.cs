using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
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

    // How long after an invalidation or clear it's published again, at least. FusionCache 2.9 writes a tag's marker
    // without the lock its tag check holds, and the check writes what it read unconditionally, so an invalidation landing
    // inside a check on the same node is overwritten (FusionCache#659). The losing check leaves its marker in memory, so
    // a second publish sticks unless another check's distributed-cache read outlasts the delay: 5 s is
    // StackExchange.Redis's default timeout, so such a read has failed by then. Re-publishes are batched by the whole
    // second they're due at (see PublishAgainLater), so each waits up to a second more.
    internal static readonly TimeSpan RepublishDelay = TimeSpan.FromSeconds(5);

    private readonly IFusionCache _cache;
    private readonly IServiceProvider _services;
    private readonly FusionCacheEntryOptions _entryOptions;
    private readonly FusionCacheEntryOptions _tagOptions;
    private readonly TimeProvider _time;
    private volatile Func<Transaction, IReadOnlyCollection<Dependency>>? _ambientWrites;
    private volatile Func<ValueTask>? _beforeRead;
    private readonly Func<object, Exception?>[] _storeChecks;

    // Each re-publish window waiting or publishing, removed once it has published, so dispose can fire them and wait
    // for them before FusionCache is disposed after it; the latest, which an invalidation due at the same second joins
    // until it fires; and whether dispose has begun, after which a window opened fires at once. All guarded by locking
    // the set.
    private readonly HashSet<RepublishWindow> _republishing = [];
    private RepublishWindow? _joinable;
    private bool _disposed;

    internal SluiceCache(
        IFusionCache cache,
        IServiceProvider services,
        FusionCacheEntryOptions tagOptions,
        FusionCacheInternalStrings internalStrings,
        TimeProvider time,
        Func<object, Exception?>[] storeChecks
    )
    {
        _cache = cache;
        _services = services;
        _tagOptions = tagOptions;
        _time = time;
        _storeChecks = storeChecks;

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

    // What a package runs before every read: publishing writes whose transaction ended without telling it.
    internal void BeforeRead(Func<ValueTask> step) => _beforeRead = step;

    // The clock the cache times its re-publishes on, for a package's own timers, so a test's fake clock drives both.
    internal TimeProvider Time => _time;

    // Whether dispose has begun, for a package's timer to stop publishing to a cache that's going away.
    internal bool IsDisposed
    {
        get
        {
            lock (_republishing)
            {
                return _disposed;
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
            query.MaxStaleness,
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
        return GetOrSetAsync(
            query.Name,
            query.Name,
            query.Duration,
            query.Size,
            query.MaxStaleness,
            compute,
            ct
        );
    }

    private async ValueTask<TValue> GetOrSetAsync<TValue>(
        string name,
        string entryKey,
        TimeSpan? duration,
        long? size,
        TimeSpan? maxStaleness,
        Func<SluiceReads, CancellationToken, Task<TValue>> compute,
        CancellationToken ct
    )
    {
        // An already-cancelled caller gets neither a hit nor a compute.
        ct.ThrowIfCancellationRequested();
        if (_beforeRead is { } beforeRead)
        {
            await beforeRead().ConfigureAwait(false);
        }

        var (options, copyOptions) = OptionsFor(name, duration, size, maxStaleness);
        var reads = new SluiceReads(name, _services, ct);

        // A value cached or computed outside the caller's transaction lacks its uncommitted writes, and a hit can't
        // be checked against them: FusionCache doesn't return an entry's tags. So the call computes fresh and leaves
        // FusionCache alone, stampede lock included, so no other caller waits on it or inherits its violation.
        if (PendingWrites() is { } pending)
        {
            return await BypassAsync(reads, compute, pending, ct).ConfigureAwait(false);
        }

        var value = copyOptions is null
            ? await GetOrSetEntryAsync(entryKey, options, reads, compute, stored: null, ct)
                .ConfigureAwait(false)
            : await GetStaleOrSetAsync(entryKey, options, copyOptions, reads, compute, ct)
                .ConfigureAwait(false);

        reads.ThrowIfViolated();
        return value;
    }

    // The normal entry's FusionCache call, which a strict query makes directly and a staleness query's copy factory
    // makes inside the copy's. The caller's token goes to the compute, not to FusionCache: on a cancelled token
    // FusionCache throws without awaiting its factory, so the caller would resume while the compute still uses its
    // resources. Cancelling ends the compute first, and the caller resumes after it.
    private ValueTask<TValue> GetOrSetEntryAsync<TValue>(
        string entryKey,
        FusionCacheEntryOptions options,
        SluiceReads reads,
        Func<SluiceReads, CancellationToken, Task<TValue>> compute,
        StrongBox<string[]?>? stored,
        CancellationToken ct
    ) =>
        _cache.GetOrSetAsync<TValue>(
            entryKey,
            (ctx, _) => FactoryAsync(reads, compute, _storeChecks, stored, ctx, ct),
            options: options
        );

    // A query with MaxStaleness (SLUICE-CORE-024) reads its normal entry, then its stale copy, and only then computes,
    // through the copy's factory, so the copy is stamped before the compute begins: a hard eviction landing mid-compute
    // still removes it. Neither lookup takes the caller's token (SLUICE-CORE-003), and both options refuse a logically
    // expired entry: fail-safe keeps the normal entry past its Duration, and a node that read the copy from the
    // distributed cache keeps it in memory past N. The copy's lock is taken before the normal entry's, as FusionCache's
    // own tag check takes a tag's while it holds an entry's, and strict queries never take it.
    private async ValueTask<TValue> GetStaleOrSetAsync<TValue>(
        string entryKey,
        FusionCacheEntryOptions options,
        FusionCacheEntryOptions copyOptions,
        SluiceReads reads,
        Func<SluiceReads, CancellationToken, Task<TValue>> compute,
        CancellationToken ct
    )
    {
        if (
            await _cache.TryGetAsync<TValue>(entryKey, options).ConfigureAwait(false) is
            { HasValue: true } entry
        )
        {
            return entry.Value;
        }

        // No entry key or tag-data key starts with ':' (SLUICE-CORE-009).
        var copyKey = $":{entryKey}";
        if (
            await _cache.TryGetAsync<TValue>(copyKey, copyOptions).ConfigureAwait(false) is
            { HasValue: true } copy
        )
        {
            return copy.Value;
        }

        return await _cache
            .GetOrSetAsync<TValue>(
                copyKey,
                (ctx, _) => CopyFactoryAsync(entryKey, options, reads, compute, ctx, ct),
                options: copyOptions
            )
            .ConfigureAwait(false);
    }

    // The stale copy's factory: makes the normal entry's call, then keeps its value as the copy, tagged with the
    // compute's now-tags, for what's left of N since this factory started. Only a compute of this caller's that stored
    // the normal entry leaves a copy: when the normal entry's call returned without that (a violation, DoNotCache, an
    // inner hit, whose tags it can't know and whose node wrote its own copy, or a fail-safe value after the compute
    // threw), or once N has passed, it returns the value with every write skipped, as FactoryAsync does, so nothing is
    // served over it. The copy's options have fail-safe off, so the compute's own exception passes through with nothing
    // served.
    private async Task<TValue> CopyFactoryAsync<TValue>(
        string entryKey,
        FusionCacheEntryOptions options,
        SluiceReads reads,
        Func<SluiceReads, CancellationToken, Task<TValue>> compute,
        FusionCacheFactoryExecutionContext<TValue> ctx,
        CancellationToken ct
    )
    {
        // FusionCache stamps the copy and sets its expiry on DateTimeOffset.UtcNow, not on the registered TimeProvider,
        // so the time the compute took is measured on that clock too, to end the copy N after this started. A fake
        // TimeProvider that doesn't move at real time would leave the compute's time out, and the copy would outlive N
        // by that much.
        var start = DateTimeOffset.UtcNow;
        var stored = new StrongBox<string[]?>();
        var value = await GetOrSetEntryAsync(entryKey, options, reads, compute, stored, ct)
            .ConfigureAwait(false);

        var copyOptions = ctx.Options;
        var life = copyOptions.Duration - (DateTimeOffset.UtcNow - start);
        if (stored.Value is not { } tags || life <= TimeSpan.Zero)
        {
            copyOptions.SkipMemoryCacheWrite = true;
            copyOptions.SkipDistributedCacheWrite = true;
            copyOptions.SkipBackplaneNotifications = true;
            return value;
        }

        // The memory entry's expiry counts from now, so it gets what's left of N. The distributed cache's counts from
        // the copy's stamp, taken before this factory started, so it gets N itself: what's left would shorten it twice,
        // and other nodes, which read the copy from there, would lose it early, or on arrival once the compute took
        // half of N.
        copyOptions.DistributedCacheDuration = copyOptions.Duration;
        copyOptions.Duration = life;
        ctx.Tags = Dependency.NowTags(tags);
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
        var (value, tags, _) = await ComputeAsync(reads, compute, storeChecks: [], ct)
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
    /// again 5 to 6 s later in the background, in one batch with every other invalidation and clear due in the same
    /// second, since FusionCache 2.9 can lose one that lands while the same server checks those dependencies; a value
    /// cached in between is evicted again, and disposing the cache publishes it at once. A query with
    /// <c>MaxStaleness</c> keeps serving what it computed for up to that long; for data that must go at once, use
    /// <see cref="InvalidateNowAsync(IEnumerable{Dependency})"/>, or call this inside <see cref="SluiceFlow.InvalidateNow"/>,
    /// which makes it publish as that does.
    /// </remarks>
    public ValueTask InvalidateAsync(IEnumerable<Dependency> dependencies) =>
        InvalidateByAsync(dependencies, PublishedOnThisFlow());

    /// <summary>The synchronous twin of <see cref="InvalidateAsync(IEnumerable{Dependency})"/>.</summary>
    /// <param name="dependencies">What was written.</param>
    /// <inheritdoc cref="InvalidateAsync(IEnumerable{Dependency})" path="/remarks"/>
    public void Invalidate(params IEnumerable<Dependency> dependencies) =>
        InvalidateBy(dependencies, PublishedOnThisFlow());

    // Read on the caller's flow, before anything is awaited. Inside SluiceFlow.InvalidateNow every dependency publishes
    // as if made hard first, its hard tags, so the app's own calls there publish as InvalidateNowAsync does.
    private static Func<Dependency, string[]> PublishedOnThisFlow() =>
        SluiceFlow.Now
            ? dependency => dependency.HardInvalidationTags
            : dependency => dependency.PublishedTags;

    // What a package captured (FlowWrites.Publish): each dependency publishes as it was marked when its write started,
    // whatever flow this runs on, so the scope isn't read here.
    internal ValueTask PublishAsync(IEnumerable<Dependency> dependencies) =>
        InvalidateByAsync(dependencies, dependency => dependency.PublishedTags);

    internal void Publish(IEnumerable<Dependency> dependencies) =>
        InvalidateBy(dependencies, dependency => dependency.PublishedTags);

    /// <summary>
    /// Evicts every cached value that read <paramref name="dependency"/>, the values a query with <c>MaxStaleness</c>
    /// would still serve included. Call it after your write commits, for data that must go at once, such as an erasure.
    /// </summary>
    /// <param name="dependency">What was written.</param>
    /// <inheritdoc cref="InvalidateNowAsync(IEnumerable{Dependency})" path="/remarks"/>
    public ValueTask InvalidateNowAsync(Dependency dependency)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        return InvalidateNowAsync([dependency]);
    }

    /// <summary>
    /// Evicts every cached value that read any of <paramref name="dependencies"/>, the values a query with
    /// <c>MaxStaleness</c> would still serve included. Call it after your write commits, for data that must go at once,
    /// such as an erasure.
    /// </summary>
    /// <param name="dependencies">What was written.</param>
    /// <remarks>
    /// Behaves as <see cref="InvalidateAsync(IEnumerable{Dependency})"/> does: it takes no cancellation token, a
    /// distributed cache write that fails doesn't make it throw, a slow one makes it wait, and it's published again 5 to
    /// 6 s later in the background. It writes twice the tags, so use <see cref="InvalidateAsync(IEnumerable{Dependency})"/>
    /// for writes that can lag.
    /// </remarks>
    public ValueTask InvalidateNowAsync(IEnumerable<Dependency> dependencies) =>
        InvalidateByAsync(dependencies, dependency => dependency.HardInvalidationTags);

    /// <summary>The synchronous twin of <see cref="InvalidateNowAsync(IEnumerable{Dependency})"/>.</summary>
    /// <param name="dependencies">What was written.</param>
    /// <inheritdoc cref="InvalidateNowAsync(IEnumerable{Dependency})" path="/remarks"/>
    public void InvalidateNow(params IEnumerable<Dependency> dependencies) =>
        InvalidateBy(dependencies, dependency => dependency.HardInvalidationTags);

    private async ValueTask InvalidateByAsync(
        IEnumerable<Dependency> dependencies,
        Func<Dependency, string[]> tagsOf
    )
    {
        var tags = Dependency.TagsOf(dependencies, tagsOf);
        if (tags.Count == 0)
        {
            return;
        }

        await RemoveByTagsAsync(tags).ConfigureAwait(false);
        PublishAgainLater(tags, clear: false);
    }

    private void InvalidateBy(
        IEnumerable<Dependency> dependencies,
        Func<Dependency, string[]> tagsOf
    )
    {
        var tags = Dependency.TagsOf(dependencies, tagsOf);
        if (tags.Count == 0)
        {
            return;
        }

        _cache.RemoveByTag(tags);
        PublishAgainLater(tags, clear: false);
    }

    /// <summary>Evicts everything Sluice cached.</summary>
    /// <param name="ct">Cancels the clear.</param>
    /// <remarks>
    /// Once it returns, the clear is published again 5 to 6 s later in the background, as an invalidation is (see
    /// <see cref="InvalidateAsync(IEnumerable{Dependency})"/>), so a value cached in between is evicted too.
    /// </remarks>
    public async ValueTask ClearAsync(CancellationToken ct = default)
    {
        await _cache.ClearAsync(allowFailSafe: false, token: ct).ConfigureAwait(false);
        PublishAgainLater([], clear: true);
    }

    /// <summary>
    /// Publishes at once every invalidation and clear still waiting to be published again, and waits for them. Never
    /// throws: FusionCache logs a failed distributed cache or backplane write.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // Marked under the lock that opens windows, so each is either fired here or fired by its opener.
        RepublishWindow[] windows;
        lock (_republishing)
        {
            _disposed = true;
            windows = [.. _republishing];
        }

        foreach (var window in windows)
        {
            window.Timer?.Dispose();
            window.Fire();
        }

        // Each window's own publish, so a failure in one would reach this dispose rather than go unobserved.
        await Task.WhenAll(windows.Select(window => window.Publishing)).ConfigureAwait(false);
    }

    /// <summary>
    /// The synchronous twin of <see cref="DisposeAsync"/>, for a service provider disposed synchronously, which
    /// refuses a service that can only be disposed asynchronously.
    /// </summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    // FusionCache's multi-tag overload removes one tag at a time, so run them side by side.
    private Task RemoveByTagsAsync(IEnumerable<string> tags) =>
        Task.WhenAll(
            tags.Select(tag => _cache.RemoveByTagAsync(tag, token: CancellationToken.None).AsTask())
        );

    // Adds the tags, or the clear, to the window due at the first whole second at least the delay from now, opening it
    // if no invalidation has yet: one timer and one de-duplicated tag set per second, however many invalidations. Timed
    // on the monotonic timestamp, not the wall clock, which can step back and shorten a joiner's wait below the delay.
    // Callers read the time before the lock, so they can take it out of order: one that finds a window due later than
    // its own joins it, since it still waits at least the delay, rather than open a second window for a second gone by.
    private void PublishAgainLater(IEnumerable<string> tags, bool clear)
    {
        var second = _time.TimestampFrequency;
        var now = _time.GetTimestamp();
        var due =
            (now + RepublishDelay.Ticks * second / TimeSpan.TicksPerSecond + second - 1)
            / second
            * second;
        RepublishWindow? openedAfterDispose = null;
        lock (_republishing)
        {
            if (_joinable is null || due > _joinable.Due)
            {
                _joinable = Open(due, _time.GetElapsedTime(now, due));
                openedAfterDispose = _disposed ? _joinable : null;
            }

            _joinable.Tags.UnionWith(tags);
            _joinable.Clear |= clear;
        }

        // Outside the lock, since the publish runs on the thread that fires it.
        openedAfterDispose?.Fire();
    }

    // The window outlives the call that opened it, so neither its timer nor its wait holds that caller's async state
    // (its request, transaction or trace). Its timer exists when the call returns; none once the cache is disposed.
    private RepublishWindow Open(long due, TimeSpan delay)
    {
        var window = new RepublishWindow(due);
        if (ExecutionContext.IsFlowSuppressed())
        {
            Start(window, delay);
        }
        else
        {
            using (ExecutionContext.SuppressFlow())
            {
                Start(window, delay);
            }
        }

        _republishing.Add(window);
        return window;
    }

    private void Start(RepublishWindow window, TimeSpan delay)
    {
        window.Publishing = RepublishAsync(window);
        if (!_disposed)
        {
            window.Timer = _time.CreateTimer(
                state => Elapsed((RepublishWindow)state!),
                window,
                WholeMilliseconds(delay),
                Timeout.InfiniteTimeSpan
            );
        }
    }

    // The system timer runs on a coarser clock than the timestamp a window is due on, so it can fire up to one of that
    // clock's ticks (a jiffy, or 15.6 ms on Windows) early: then it's set again for what's left rather than fired. Under
    // the lock, after the timer is set and before dispose disposes it, so it's never set again once disposed.
    private void Elapsed(RepublishWindow window)
    {
        var early = _time.GetElapsedTime(_time.GetTimestamp(), window.Due);
        if (early > TimeSpan.Zero)
        {
            lock (_republishing)
            {
                if (!_disposed)
                {
                    window.Timer!.Change(WholeMilliseconds(early), Timeout.InfiniteTimeSpan);
                    return;
                }
            }
        }

        window.Fire();
    }

    // Rounded up, since the system timer counts whole milliseconds, truncated.
    private static TimeSpan WholeMilliseconds(TimeSpan delay) =>
        TimeSpan.FromMilliseconds(Math.Ceiling(delay.TotalMilliseconds));

    // Publishes the window's tags and clear once its timer or dispose fires it. Nothing awaits it but dispose, so every
    // failure ends here: FusionCache has logged a failed write, or was disposed first, at shutdown.
    private async Task RepublishAsync(RepublishWindow window)
    {
        await window.Fired.ConfigureAwait(false);

        // Closed to joiners under the lock, so its tags and clear are final once it's released.
        lock (_republishing)
        {
            if (_joinable == window)
            {
                _joinable = null;
            }
        }

        try
        {
            await Task.WhenAll(
                    RemoveByTagsAsync(window.Tags),
                    window.Clear
                        ? _cache.ClearAsync(allowFailSafe: false).AsTask()
                        : Task.CompletedTask
                )
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // FusionCache logged the failed write, or was disposed first, at shutdown.
        }
        finally
        {
            lock (_republishing)
            {
                _republishing.Remove(window);
            }
        }
    }

    // The re-publishes due at one whole second: every invalidation and clear in the second that ends the delay before
    // it, published again together.
    private sealed class RepublishWindow(long due)
    {
        // Completed by the timer or by dispose, whichever is first. Its continuations run on that thread, so the
        // publish starts there, as soon as the window is due.
        private readonly TaskCompletionSource _fired = new();

        public long Due { get; } = due;

        public HashSet<string> Tags { get; } = new(StringComparer.Ordinal);

        public bool Clear { get; set; }

        public ITimer? Timer { get; set; }

        public Task Publishing { get; set; } = Task.CompletedTask;

        public Task Fired => _fired.Task;

        public void Fire() => _fired.TrySetResult();
    }

    // The normal entry's options, and the stale copy's for a query with MaxStaleness (SLUICE-CORE-024). A query with
    // none of its own settings shares the pinned defaults, with no allocation.
    private (FusionCacheEntryOptions Normal, FusionCacheEntryOptions? Copy) OptionsFor(
        string name,
        TimeSpan? duration,
        long? size,
        TimeSpan? maxStaleness
    )
    {
        if (duration is null && size is null && maxStaleness is null)
        {
            return (_entryOptions, null);
        }

        var options = _entryOptions.Duplicate(duration);
        options.Size = size ?? options.Size;

        if (duration is not null)
        {
            EntryLifetime.EnsureWithin(options, _tagOptions, $"Query '{name}'");
        }

        if (maxStaleness is not { } n)
        {
            return (options, null);
        }

        // The copy lives N from its factory's start and no longer: fail-safe, jitter, and a level's own duration would
        // each keep it past that, and a read of a logically expired entry would serve it. Its factory sets the
        // distributed cache's duration itself, to N from the copy's stamp. Its size is the query's.
        options.AllowStaleOnReadOnly = false;
        var copy = _entryOptions.Duplicate(n);
        copy.Size = options.Size;
        copy.IsFailSafeEnabled = false;
        copy.JitterMaxDuration = TimeSpan.Zero;
        copy.MemoryCacheDuration = null;
        copy.DistributedCacheDuration = null;
        copy.AllowStaleOnReadOnly = false;
        EntryLifetime.EnsureWithin(
            copy,
            _tagOptions,
            $"The MaxStaleness of query '{name}'",
            shorten: "MaxStaleness"
        );

        return (options, copy);
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
    // a guard was violated. When it stores, it puts the tags in `stored`, which a stale copy's factory passes to learn
    // that this caller's compute stored the entry, and with what.
    private static async Task<TValue> FactoryAsync<TValue>(
        SluiceReads reads,
        Func<SluiceReads, CancellationToken, Task<TValue>> compute,
        Func<object, Exception?>[] storeChecks,
        StrongBox<string[]?>? stored,
        FusionCacheFactoryExecutionContext<TValue> ctx,
        CancellationToken ct
    )
    {
        var (value, tags, uncached) = await ComputeAsync(reads, compute, storeChecks, ct)
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
        stored?.Value = tags;
        return value;
    }

    // Runs the compute, whether its value is to be stored or not: as the flow's compute, outside the caller's ambient
    // transaction, then closes it and disposes what it held. Returns the value, the tags it recorded and whether it
    // asked not to be cached; a guard violation is left on reads, for the caller to check. The compute's own exception
    // is rethrown. The store checks (none when nothing will be stored) run last, on a value that will be stored.
    private static async Task<(TValue Value, string[] Tags, bool Uncached)> ComputeAsync<TValue>(
        SluiceReads reads,
        Func<SluiceReads, CancellationToken, Task<TValue>> compute,
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
