using System.Transactions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Sluice.Infrastructure;
using Sluice.Tests.Support;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Events;

namespace Sluice.Tests;

public sealed class RaceTests
{
    private static readonly Source<int> _rows = new("race-rows");
    private static readonly Query<string, string> _view = new("race-view");

    // Sluice's re-publish delay (SluiceCache.RepublishDelay).
    private static readonly TimeSpan _republishDelay = TimeSpan.FromSeconds(5);

    private readonly SystemLikeClock _time = new();

    private readonly Dictionary<int, string> _data = new() { [1] = "old", [2] = "other" };
    private readonly TaskCompletionSource _reading = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly TaskCompletionSource _gate = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private int _runs;

    // The first run reads row 1, then waits at the gate until the test has written and invalidated.
    private Task<string> View(SluiceCache cache) =>
        cache
            .GetOrSetAsync(
                _view,
                "k",
                async (read, _) =>
                {
                    var run = ++_runs;
                    read.From(_rows.For(1));
                    var value = _data[1];
                    if (run == 1)
                    {
                        _reading.SetResult();
                        await _gate.Task;
                    }

                    return value;
                }
            )
            .AsTask();

    [Fact]
    public async Task An_overlapping_invalidation_mid_compute_returns_the_value_but_leaves_no_entry()
    {
        await using var node = new SluiceNode();
        var racing = View(node.Cache);
        await _reading.Task;

        _data[1] = "new";
        await node.Cache.InvalidateAsync(_rows.For(1));
        _gate.SetResult();

        (await racing).Should().Be("old");
        (await View(node.Cache)).Should().Be("new");
        _runs.Should().Be(2);
    }

    [Fact]
    public async Task A_non_overlapping_invalidation_mid_compute_keeps_the_entry()
    {
        await using var node = new SluiceNode();
        var racing = View(node.Cache);
        await _reading.Task;

        _data[2] = "changed";
        await node.Cache.InvalidateAsync(_rows.For(2));
        _gate.SetResult();

        (await racing).Should().Be("old");
        (await View(node.Cache)).Should().Be("old");
        _runs.Should().Be(1);
    }

    // A caller with uncommitted writes stays out of FusionCache, stampede lock included, so a caller without them,
    // computing the same key at the same time, never waits on it or gets its violation, whichever starts first.
    // Each call that mustn't wait has a timeout, so a regression fails rather than hangs.
    [Theory]
    [InlineData("the caller with uncommitted writes")]
    [InlineData("the caller without")]
    public async Task Callers_with_and_without_uncommitted_writes_on_one_key_neither_wait_on_nor_fail_each_other(
        string first
    )
    {
        await using var node = new SluiceNode();
        var mayFinish = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var quickRuns = 0;

        // Holds its compute open until the test lets it finish.
        async Task<string> Held(SluiceReads read, CancellationToken ct)
        {
            read.From(_rows.For(1));
            _reading.SetResult();
            await mayFinish.Task;
            return "held";
        }

        Task<string> Quick(SluiceReads read, CancellationToken ct)
        {
            read.From(_rows.For(1));
            return Task.FromResult($"quick#{++quickRuns}");
        }

        // Its box is set inside this method, so the test's own flow never has one.
        async Task<string> WithWrites(Func<SluiceReads, CancellationToken, Task<string>> compute)
        {
            using var flow = SluiceFlow.Begin();
            FlowWrites.AddProbe(node.Cache, "transaction", () => [_rows.For(1)]);
            return await node.Cache.GetOrSetAsync(_view, "k", compute);
        }

        Task<string> Without(Func<SluiceReads, CancellationToken, Task<string>> compute) =>
            node.Cache.GetOrSetAsync(_view, "k", compute).AsTask();

        var timeout = TimeSpan.FromSeconds(10);
        if (first == "the caller with uncommitted writes")
        {
            var withWrites = WithWrites(Held);
            await _reading.Task;

            (await Without(Quick).WaitAsync(timeout)).Should().Be("quick#1");
            mayFinish.SetResult();

            await withWrites
                .Awaiting(call => call)
                .Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("Query 'race-view' depends on race-rows:1, *");
            (await Without(Quick)).Should().Be("quick#1");
        }
        else
        {
            var without = Without(Held);
            await _reading.Task;

            await WithWrites(Quick)
                .WaitAsync(timeout)
                .Awaiting(call => call)
                .Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("Query 'race-view' depends on race-rows:1, *");
            mayFinish.SetResult();

            (await without).Should().Be("held");
            (await Without(Quick)).Should().Be("held");
        }
    }

    private void OnFakeTime(IServiceCollection services) =>
        services.AddSingleton<TimeProvider>(_time);

    // Advances the node's clock by `by` and waits for FusionCache to report `publishes` tag or clear writes, so a test
    // reads only once the re-publish has landed; `onPublish` runs on the flow of each. The bound only turns a regression
    // into a failure instead of a hang.
    private async Task AdvanceUntilPublishedAsync(
        SluiceNode node,
        TimeSpan by,
        int publishes,
        Action? onPublish = null
    )
    {
        var seen = 0;
        var published = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        void Count(object? sender, EventArgs e)
        {
            onPublish?.Invoke();
            if (Interlocked.Increment(ref seen) == publishes)
            {
                published.TrySetResult();
            }
        }

        node.Fusion.Events.RemoveByTag += Count;
        node.Fusion.Events.Clear += Count;
        try
        {
            await AdvanceAsATimerAsync(by);
            await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            node.Fusion.Events.RemoveByTag -= Count;
            node.Fusion.Events.Clear -= Count;
        }
    }

    // Advances the clock on a pool thread with a flow of its own, as a real timer fires. A re-publish runs on the thread
    // that fires its window, so advancing from the test's flow would lend it the test's async state; and the test's
    // thread has xUnit's synchronization context, which makes it run on another thread instead, maybe after this returns.
    // From a pool thread it runs inside the advance, so with in-memory caches, whose writes complete at once, whatever
    // the clock made due has been published when this returns, and a read after it sees a re-publish that came early.
    private async Task AdvanceAsATimerAsync(TimeSpan by)
    {
        Task advancing;
        using (ExecutionContext.SuppressFlow())
        {
            advancing = Task.Run(() => _time.Advance(by));
        }

        await advancing;
    }

    // FusionCache 2.9 (FusionCache#659): a tag check re-reads the tag's marker under its lock and then writes what it
    // read, while RemoveByTag writes the marker without that lock, so an invalidation landing in between is overwritten
    // and the invalidated value is served. The memory cache here invalidates at exactly that point: on the check's
    // second miss of the marker, its re-read under the lock. If the precondition fails, FusionCache has fixed the race:
    // remove the re-publish (SLUICE-CORE-022) and this test.
    [Fact]
    public async Task An_invalidation_a_concurrent_tag_check_overwrote_is_restored_when_it_is_published_again()
    {
        var memory = new HookedMemoryCache();
        await using var node = new SluiceNode(cache => cache.WithMemoryCache(memory), OnFakeTime);
        var runs = 0;
        Task<string> Read() =>
            node
                .Cache.GetOrSetAsync(
                    _view,
                    "k",
                    (read, _) =>
                    {
                        runs++;
                        read.From(_rows.For(1));
                        return Task.FromResult(_data[1]);
                    }
                )
                .AsTask();
        await Read();

        _data[1] = "new";
        var invalidated = false;
        memory.OnMiss = (key, misses) =>
        {
            if (key is "sluice:__fc:t:race-rows:1" && misses == 2)
            {
                node.Cache.Invalidate(_rows.For(1));
                invalidated = true;
            }
        };
        await Read();
        memory.OnMiss = null;

        invalidated.Should().BeTrue("the hook ran inside the tag check");
        (await Read())
            .Should()
            .Be("old", "FusionCache's tag check overwrote the invalidation (FusionCache#659)");
        await AdvanceUntilPublishedAsync(node, _republishDelay, publishes: 2);
        (await Read()).Should().Be("new");
        runs.Should().Be(2);
    }

    [Theory]
    [InlineData("InvalidateAsync(dependency)")]
    [InlineData("InvalidateAsync(dependencies)")]
    [InlineData("Invalidate")]
    public async Task Each_invalidation_is_published_again_after_the_delay_and_not_before(
        string call
    )
    {
        await using var node = new SluiceNode(services: OnFakeTime);
        var reader = new CountingReader(node.Cache, _view);
        await reader.GetAsync("k", _rows.For(1));

        switch (call)
        {
            case "InvalidateAsync(dependency)":
                await node.Cache.InvalidateAsync(_rows.For(1));
                break;
            case "InvalidateAsync(dependencies)":
                await node.Cache.InvalidateAsync([_rows.For(1)]);
                break;
            default:
                node.Cache.Invalidate(_rows.For(1));
                break;
        }

        (await reader.GetAsync("k", _rows.For(1))).Should().Be("k#2");
        await AdvanceAsATimerAsync(_republishDelay - TimeSpan.FromTicks(1));
        (await reader.GetAsync("k", _rows.For(1))).Should().Be("k#2", "the delay hasn't passed");

        // The key's tag and the whole-source readers' tag.
        await AdvanceUntilPublishedAsync(node, TimeSpan.FromTicks(1), publishes: 2);
        (await reader.GetAsync("k", _rows.For(1)))
            .Should()
            .Be("k#3", "the value cached in between was evicted");

        await AdvanceAsATimerAsync(_republishDelay);
        (await reader.GetAsync("k", _rows.For(1))).Should().Be("k#3", "it's published again once");
    }

    // Node A's pending re-publish reaches node B as it's disposed, so B's value cached in between is evicted. A's
    // distributed cache write holds the re-publish until the test lets it go, so the dispose must still be waiting.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Disposing_the_cache_publishes_pending_re_invalidations_at_once_waits_for_them_and_throws_nothing(
        bool asynchronously
    )
    {
        var l2 = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var gated = new GatedDistributedCache(l2);
        var bus = Guid.NewGuid().ToString();
        var a = SluiceNode.Clustered(gated, bus, services: OnFakeTime);
        await using var b = SluiceNode.Clustered(l2, bus);
        var reader = new CountingReader(b.Cache, _view);
        await reader.GetAsync("k", _rows.For(1));
        await a.Cache.InvalidateAsync(_rows.For(1));
        (await reader.GetAsync("k", _rows.For(1))).Should().Be("k#2");

        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gated.BeforeSet = () =>
        {
            writing.TrySetResult();
            return release.Task;
        };

        // The synchronous dispose blocks its thread, so it runs on another. Either is still waiting while its
        // re-publish is held at the gate.
        var disposing = asynchronously ? a.DisposeAsync().AsTask() : Task.Run(a.Dispose);
        await writing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        disposing.IsCompleted.Should().BeFalse("the re-publish is still writing");

        release.SetResult();

        // Bounded, so a dispose that waits for the delay fails rather than hangs.
        await disposing
            .Awaiting(call => call.WaitAsync(TimeSpan.FromSeconds(10)))
            .Should()
            .NotThrowAsync();
        (await reader.GetAsync("k", _rows.For(1))).Should().Be("k#3");
    }

    // The re-publish outlives the call, so it mustn't hold the caller's transaction or anything else on its flow.
    [Fact]
    public async Task A_re_publish_holds_none_of_the_caller_s_async_state()
    {
        await using var node = new SluiceNode(services: OnFakeTime);
        var callerState = new AsyncLocal<string>();
        var seen = new List<(Transaction? Transaction, string? State)>();

        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            callerState.Value = "the caller's";
            await node.Cache.InvalidateAsync(_rows.For(1));

            await AdvanceUntilPublishedAsync(
                node,
                _republishDelay,
                publishes: 2,
                onPublish: () =>
                {
                    lock (seen)
                    {
                        seen.Add((Transaction.Current, callerState.Value));
                    }
                }
            );
        }

        seen.Should().Equal((null, null), (null, null));
    }

    // A cache disposed without its provider, so FusionCache outlives it: an invalidation then publishes again at once,
    // with no clock to wait on, even one due in the same second as one the dispose published, whose window is closed.
    [Fact]
    public async Task An_invalidation_after_dispose_is_published_again_at_once()
    {
        await using var node = new SluiceNode(services: OnFakeTime);
        var tags = 0;
        var republished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        node.Fusion.Events.RemoveByTag += (_, _) =>
        {
            // Each invalidation's key tag and whole-source readers' tag, published and then published again.
            if (Interlocked.Increment(ref tags) == 8)
            {
                republished.TrySetResult();
            }
        };

        await node.Cache.InvalidateAsync(_rows.For(1));
        await node.Cache.DisposeAsync();
        await node.Cache.InvalidateAsync(_rows.For(2));

        await republished.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // Dispose awaits each window's publish, so a failing one must end in Sluice: the dispose doesn't throw, and the
    // failure is seen where it belongs, in FusionCache's log or its refusal once disposed. Without Sluice's catch, the
    // dispose would throw it.
    [Theory]
    [InlineData("FusionCache was disposed first")]
    [InlineData("FusionCache's memory write throws")]
    public async Task A_failing_re_publish_reaches_no_one(string failure)
    {
        var memory = new HookedMemoryCache();
        var log = new FakeLogger<FusionCache>();
        await using var node = new SluiceNode(
            cache => cache.WithMemoryCache(memory).WithLogger(log),
            OnFakeTime
        );
        var thrown = new InvalidOperationException("Simulated memory cache failure.");

        await node.Cache.InvalidateAsync(_rows.For(1));
        if (failure == "FusionCache was disposed first")
        {
            node.Fusion.Dispose();
            await node
                .Fusion.Awaiting(cache => cache.RemoveByTagAsync("race-rows:1"))
                .Should()
                .ThrowAsync<ObjectDisposedException>("the re-publish's tag writes fail");
        }
        else
        {
            memory.OnWrite = _ => throw thrown;
        }

        try
        {
            await node.Cache.Awaiting(cache => cache.DisposeAsync()).Should().NotThrowAsync();
        }
        finally
        {
            memory.OnWrite = null;
        }

        if (failure == "FusionCache's memory write throws")
        {
            // The key's tag and the whole-source readers' tag.
            log.Collector.GetSnapshot()
                .Where(record => record.Level == LogLevel.Error)
                .Select(record => record.Exception)
                .Should()
                .Equal(thrown, thrown);
        }
    }

    // Every invalidation and clear due at the same whole second is published again in one batch, each tag once however
    // often it was invalidated. The clock starts on a whole second, S, so these are all due at S + 6 s: the first, a
    // tick after S, is published again 6 s later (its timer counts whole milliseconds, rounded up), and the last, a tick
    // before S + 1 s, not yet 5 s later.
    [Fact]
    public async Task Invalidations_due_in_the_same_second_are_published_again_together_and_each_after_the_delay()
    {
        // Handlers run on the publishing thread, inside the advance (AdvanceAsATimerAsync), so each reports what it
        // published once it returns.
        await using var node = new SluiceNode(
            cache => cache.WithOptions(options => options.EnableSyncEventHandlersExecution = true),
            OnFakeTime
        );
        (_time.GetTimestamp() % _time.TimestampFrequency)
            .Should()
            .Be(0, "the clock starts on a whole second");
        var published = new List<string>();
        void Tag(object? sender, FusionCacheTagEventArgs e) => published.Add(e.Tag);
        void Clear(object? sender, EventArgs e) => published.Add("clear");

        _time.Advance(TimeSpan.FromTicks(1));
        await node.Cache.InvalidateAsync(_rows.For(1));
        _time.Advance(TimeSpan.FromMilliseconds(500));
        node.Cache.Invalidate(_rows.For(2), _rows.For(1));
        await node.Cache.ClearAsync();
        _time.Advance(TimeSpan.FromMilliseconds(500) - TimeSpan.FromTicks(2));
        await node.Cache.InvalidateAsync([_rows.For(3)]);

        node.Fusion.Events.RemoveByTag += Tag;
        node.Fusion.Events.Clear += Clear;
        try
        {
            await AdvanceAsATimerAsync(_republishDelay);
            published
                .Should()
                .BeEmpty(
                    "the last invalidation was exactly 5 s ago, and its window is due after that"
                );

            await AdvanceAsATimerAsync(TimeSpan.FromTicks(2));
            published
                .Should()
                .BeEquivalentTo(
                    ["race-rows:1", "race-rows:2", "race-rows:3", "race-rows:*", "clear"],
                    "the first invalidation was 6 s ago (the keys' tags, the whole-source readers' tag and the clear)"
                );

            await AdvanceAsATimerAsync(_republishDelay * 2);
            published.Should().HaveCount(5, "each is published again once");
        }
        finally
        {
            node.Fusion.Events.RemoveByTag -= Tag;
            node.Fusion.Events.Clear -= Clear;
        }
    }

    // An NTP correction can step the wall clock back. The delay is timed on the monotonic timestamp, so an invalidation
    // made after the step doesn't join the window of one made before it, which is due sooner.
    [Fact]
    public async Task A_wall_clock_stepped_back_does_not_shorten_the_delay()
    {
        await using var node = new SluiceNode(
            cache => cache.WithOptions(options => options.EnableSyncEventHandlersExecution = true),
            OnFakeTime
        );
        var published = new List<string>();
        node.Fusion.Events.RemoveByTag += (_, e) => published.Add(e.Tag);

        _time.Advance(TimeSpan.FromMilliseconds(500));
        await node.Cache.InvalidateAsync(_rows.For(1));
        _time.WallClockStep = -TimeSpan.FromSeconds(1);
        _time.Advance(TimeSpan.FromMilliseconds(700));
        await node.Cache.InvalidateAsync(_rows.For(2));
        published.Clear();

        await AdvanceAsATimerAsync(TimeSpan.FromMilliseconds(4800));
        published.Should().Contain("race-rows:1", "it was invalidated 5.5 s ago");
        published.Should().NotContain("race-rows:2", "it was invalidated 4.8 s ago");

        await AdvanceAsATimerAsync(TimeSpan.FromSeconds(1));
        published.Should().Contain("race-rows:2");
    }

    // An invalidation reads the time before it takes the windows' lock, so under contention invalidations take it out
    // of order. One that read the time just before a second ended, and takes the lock after one that read it just after,
    // joins that one's window, due a second later, rather than open a second window, due first. The clock starts on a
    // whole second, S.
    [Fact]
    public async Task An_invalidation_that_takes_the_lock_after_a_later_one_joins_its_window()
    {
        await using var node = new SluiceNode(
            cache => cache.WithOptions(options => options.EnableSyncEventHandlersExecution = true),
            OnFakeTime
        );
        var published = new List<string>();
        node.Fusion.Events.RemoveByTag += (_, e) => published.Add(e.Tag);

        _time.Advance(TimeSpan.FromSeconds(1) - TimeSpan.FromTicks(1));
        _time.AfterNextTimestamp = () =>
        {
            _time.Advance(TimeSpan.FromTicks(2));
            node.Cache.Invalidate(_rows.For(2));
        };
        node.Cache.Invalidate(_rows.For(1));
        published.Clear();

        // Past the whole millisecond a window of its own, due at S + 6 s, would have been timed to.
        await AdvanceAsATimerAsync(_republishDelay + TimeSpan.FromMilliseconds(1));
        published
            .Should()
            .BeEmpty(
                "row 1's invalidation, due at S + 6 s on its own, joined row 2's window, due at S + 7 s"
            );

        await AdvanceAsATimerAsync(TimeSpan.FromSeconds(1));
        published.Should().BeEquivalentTo(["race-rows:1", "race-rows:2", "race-rows:*"]);
    }

    // The system timer runs on a coarser clock than the timestamp a window is due on, so it can fire up to one of that
    // clock's ticks early (15.6 ms on Windows). The window then waits out the rest rather than publish early.
    [Fact]
    public async Task A_timer_that_fires_early_does_not_publish_early()
    {
        var early = TimeSpan.FromMilliseconds(15.6);
        _time.TimersFireEarlyBy = early;
        await using var node = new SluiceNode(
            cache => cache.WithOptions(options => options.EnableSyncEventHandlersExecution = true),
            OnFakeTime
        );
        var published = new List<string>();
        node.Fusion.Events.RemoveByTag += (_, e) => published.Add(e.Tag);
        await node.Cache.InvalidateAsync(_rows.For(1));
        published.Clear();

        await AdvanceAsATimerAsync(_republishDelay - early);
        published.Should().BeEmpty("the timer fired, but the window is due 15.6 ms later");
        await AdvanceAsATimerAsync(early - TimeSpan.FromTicks(1));
        published.Should().BeEmpty("the window is due a tick later");

        // Within the whole millisecond the rest is rounded up to.
        await AdvanceAsATimerAsync(TimeSpan.FromMilliseconds(1));
        published.Should().BeEquivalentTo(["race-rows:1", "race-rows:*"]);
    }

    [Fact]
    public async Task ClearAsync_is_published_again_after_the_delay()
    {
        var l2 = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        await using var node = SluiceNode.Clustered(
            l2,
            Guid.NewGuid().ToString(),
            services: OnFakeTime
        );
        var reader = new CountingReader(node.Cache, _view);
        await reader.GetAsync("k", _rows.For(1));

        await node.Cache.ClearAsync();
        (await reader.GetAsync("k", _rows.For(1))).Should().Be("k#2");
        await AdvanceAsATimerAsync(_republishDelay - TimeSpan.FromTicks(1));
        (await reader.GetAsync("k", _rows.For(1))).Should().Be("k#2", "the delay hasn't passed");

        await AdvanceUntilPublishedAsync(node, TimeSpan.FromTicks(1), publishes: 1);
        (await reader.GetAsync("k", _rows.For(1))).Should().Be("k#3");
    }

    /// <summary>
    /// A fake clock whose timers act as the system's: they count whole milliseconds, truncated, so a delay that wasn't
    /// rounded up fires early; they can fire early by <see cref="TimersFireEarlyBy"/>, as the system's coarse timer
    /// clock makes them; and they run their callback on the flow of the code that created them, unless it suppressed
    /// that flow. Its wall clock can be stepped apart from its monotonic timestamp, as an NTP correction steps the
    /// system's.
    /// </summary>
    private sealed class SystemLikeClock : FakeTimeProvider
    {
        /// <summary>How far the wall clock (<see cref="GetUtcNow"/>) has been stepped from the monotonic timestamp.</summary>
        public TimeSpan WallClockStep { get; set; }

        /// <summary>How much earlier than it's due a new timer fires. One set again with Change fires on time.</summary>
        public TimeSpan TimersFireEarlyBy { get; set; }

        /// <summary>Runs once, in the next <see cref="GetTimestamp"/>, after it has read the time it returns.</summary>
        public Action? AfterNextTimestamp { get; set; }

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + WallClockStep;

        public override long GetTimestamp()
        {
            var now = base.GetUtcNow().Ticks;
            var after = AfterNextTimestamp;
            AfterNextTimestamp = null;
            after?.Invoke();
            return now;
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        )
        {
            // As System.Threading.Timer does; FakeTimeProvider's own run on the flow of whatever advances the clock.
            var flow = ExecutionContext.Capture();
            return base.CreateTimer(
                flow is null
                    ? callback
                    : value => ExecutionContext.Run(flow, new ContextCallback(callback), value),
                state,
                WholeMilliseconds(dueTime) - TimersFireEarlyBy,
                WholeMilliseconds(period)
            );
        }

        private static TimeSpan WholeMilliseconds(TimeSpan time) =>
            TimeSpan.FromTicks(
                time.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond
            );
    }

    /// <summary>A distributed cache that runs <see cref="BeforeSet"/> before each asynchronous write, and awaits it.</summary>
    private sealed class GatedDistributedCache(IDistributedCache inner) : IDistributedCache
    {
        public Func<Task>? BeforeSet { get; set; }

        public async Task SetAsync(
            string key,
            byte[] value,
            DistributedCacheEntryOptions options,
            CancellationToken token = default
        )
        {
            if (BeforeSet is { } before)
            {
                await before().ConfigureAwait(false);
            }

            await inner.SetAsync(key, value, options, token).ConfigureAwait(false);
        }

        public byte[]? Get(string key) => inner.Get(key);

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
            inner.GetAsync(key, token);

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
            inner.Set(key, value, options);

        public void Refresh(string key) => inner.Refresh(key);

        public Task RefreshAsync(string key, CancellationToken token = default) =>
            inner.RefreshAsync(key, token);

        public void Remove(string key) => inner.Remove(key);

        public Task RemoveAsync(string key, CancellationToken token = default) =>
            inner.RemoveAsync(key, token);
    }

    /// <summary>
    /// A memory cache that reports each miss of a key, counted from when <see cref="OnMiss"/> is set, and each write,
    /// before it's made.
    /// </summary>
    private sealed class HookedMemoryCache : IMemoryCache
    {
        public Action<object>? OnWrite { get; set; }

        private readonly MemoryCache _inner = new(new MemoryCacheOptions());
        private readonly Dictionary<object, int> _misses = [];
        private Action<object, int>? _onMiss;

        public Action<object, int>? OnMiss
        {
            get => _onMiss;
            set
            {
                lock (_misses)
                {
                    _misses.Clear();
                }

                _onMiss = value;
            }
        }

        public bool TryGetValue(object key, out object? value)
        {
            if (_inner.TryGetValue(key, out value))
            {
                return true;
            }

            if (_onMiss is { } onMiss)
            {
                int misses;
                lock (_misses)
                {
                    _misses[key] = misses = _misses.GetValueOrDefault(key) + 1;
                }

                onMiss(key, misses);
            }

            return false;
        }

        public ICacheEntry CreateEntry(object key)
        {
            OnWrite?.Invoke(key);
            return _inner.CreateEntry(key);
        }

        public void Remove(object key) => _inner.Remove(key);

        public void Dispose() => _inner.Dispose();
    }
}
