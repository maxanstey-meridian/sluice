using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Sluice.Infrastructure;
using Sluice.Tests.Support;
using ZiggyCreatures.Caching.Fusion;

namespace Sluice.Tests;

public sealed class RaceTests
{
    private static readonly Source<int> _rows = new("race-rows");
    private static readonly Query<string, string> _view = new("race-view");

    // Sluice's re-publish delay (SluiceCache.RepublishDelay).
    private static readonly TimeSpan _republishDelay = TimeSpan.FromSeconds(5);

    private readonly FakeTimeProvider _time = new();

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
    // reads only once the re-publish has landed. The bound only turns a regression into a failure instead of a hang.
    private async Task AdvanceUntilPublishedAsync(SluiceNode node, TimeSpan by, int publishes)
    {
        var seen = 0;
        var published = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        void Count(object? sender, EventArgs e)
        {
            if (Interlocked.Increment(ref seen) == publishes)
            {
                published.TrySetResult();
            }
        }

        node.Fusion.Events.RemoveByTag += Count;
        node.Fusion.Events.Clear += Count;
        try
        {
            _time.Advance(by);
            await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            node.Fusion.Events.RemoveByTag -= Count;
            node.Fusion.Events.Clear -= Count;
        }
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
        _time.Advance(_republishDelay - TimeSpan.FromTicks(1));
        (await reader.GetAsync("k", _rows.For(1))).Should().Be("k#2", "the delay hasn't passed");

        // The key's tag and the whole-source readers' tag.
        await AdvanceUntilPublishedAsync(node, TimeSpan.FromTicks(1), publishes: 2);
        (await reader.GetAsync("k", _rows.For(1)))
            .Should()
            .Be("k#3", "the value cached in between was evicted");

        _time.Advance(_republishDelay);
        (await reader.GetAsync("k", _rows.For(1))).Should().Be("k#3", "it's published again once");
    }

    // Node A's pending re-publish reaches node B as it's disposed, so B's value cached in between is evicted.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Disposing_the_cache_publishes_pending_re_invalidations_at_once_and_throws_nothing(
        bool asynchronously
    )
    {
        var l2 = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var bus = Guid.NewGuid().ToString();
        var a = SluiceNode.Clustered(l2, bus, services: OnFakeTime);
        await using var b = SluiceNode.Clustered(l2, bus);
        var reader = new CountingReader(b.Cache, _view);
        await reader.GetAsync("k", _rows.For(1));
        await a.Cache.InvalidateAsync(_rows.For(1));
        (await reader.GetAsync("k", _rows.For(1))).Should().Be("k#2");

        Func<Task> dispose = asynchronously
            ? () => a.DisposeAsync().AsTask()
            : () =>
            {
                a.Dispose();
                return Task.CompletedTask;
            };

        await dispose.Should().NotThrowAsync();
        (await reader.GetAsync("k", _rows.For(1))).Should().Be("k#3");
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
        _time.Advance(_republishDelay - TimeSpan.FromTicks(1));
        (await reader.GetAsync("k", _rows.For(1))).Should().Be("k#2", "the delay hasn't passed");

        await AdvanceUntilPublishedAsync(node, TimeSpan.FromTicks(1), publishes: 1);
        (await reader.GetAsync("k", _rows.For(1))).Should().Be("k#3");
    }

    /// <summary>A memory cache that reports each miss of a key, counted from when <see cref="OnMiss"/> is set.</summary>
    private sealed class HookedMemoryCache : IMemoryCache
    {
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

        public ICacheEntry CreateEntry(object key) => _inner.CreateEntry(key);

        public void Remove(object key) => _inner.Remove(key);

        public void Dispose() => _inner.Dispose();
    }
}
