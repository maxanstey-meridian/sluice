using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Sluice.Tests.Support;
using ZiggyCreatures.Caching.Fusion;

namespace Sluice.Tests;

public sealed class MultiNodeTests
{
    private static readonly Source<int> _rows = new("cluster-rows");
    private static readonly Query<string, string> _view = new("cluster-view");

    private readonly MemoryDistributedCache _l2 = new(
        Options.Create(new MemoryDistributedCacheOptions())
    );
    private readonly string _bus = Guid.NewGuid().ToString();
    private readonly Dictionary<int, string> _data = new() { [1] = "old" };
    private int _runs;

    private Task<string> View(SluiceCache cache) =>
        cache
            .GetOrSetAsync(
                _view,
                "k",
                (read, _) =>
                {
                    _runs++;
                    read.From(_rows.For(1));
                    return Task.FromResult(_data[1]);
                }
            )
            .AsTask();

    [Fact]
    public async Task An_invalidation_on_one_node_evicts_on_another()
    {
        await using var a = SluiceNode.Clustered(_l2, _bus);
        await using var b = SluiceNode.Clustered(_l2, _bus);
        await View(a.Cache);
        (await View(b.Cache)).Should().Be("old");
        _runs.Should().Be(1, "node B reads node A's entry from the shared L2");

        _data[1] = "new";
        await a.Cache.InvalidateAsync(_rows.For(1));

        (await View(b.Cache)).Should().Be("new");
        _runs.Should().Be(2);
    }

    // Node A publishes its invalidation again after the delay (SLUICE-CORE-022), through the backplane like the first,
    // so node B's value cached in between is evicted too.
    [Fact]
    public async Task A_re_invalidation_reaches_other_nodes()
    {
        var time = new FakeTimeProvider();
        await using var a = SluiceNode.Clustered(
            _l2,
            _bus,
            services: services => services.AddSingleton<TimeProvider>(time)
        );
        await using var b = SluiceNode.Clustered(_l2, _bus);
        await View(b.Cache);
        await a.Cache.InvalidateAsync(_rows.For(1));
        await View(b.Cache);
        _runs.Should().Be(2);

        var republished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        // Node A raises RemoveByTag once its tag write has returned, and that write awaits the backplane publish (tag
        // writes don't run backplane operations in the background), which MemoryBackplane completes only once node B
        // has handled the message. Node B's own Backplane.MessageReceived would be too early: B raises it before it
        // handles the message.
        var tags = 0;
        a.Fusion.Events.RemoveByTag += (_, _) =>
        {
            // The key's tag and the whole-source readers' tag.
            if (Interlocked.Increment(ref tags) == 2)
            {
                republished.TrySetResult();
            }
        };
        time.Advance(TimeSpan.FromSeconds(5));
        await republished.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await View(b.Cache);
        _runs.Should().Be(3);
    }

    [Fact]
    public async Task A_distributed_cache_that_fails_writes_does_not_make_InvalidateAsync_throw_and_the_tag_is_written_once_it_recovers()
    {
        var l2 = new FailingWrites();
        void Recovery(IFusionCacheBuilder cache) =>
            // FusionCache retries failed L2 writes from a background loop on this delay (5 s by default). It
            // offers no trigger and no event, so the test shortens the delay to 10 ms, FusionCache's internal
            // floor (zero turns auto-recovery off), and waits a bounded time for the rewrite below.
            cache.WithOptions(options => options.AutoRecoveryDelay = TimeSpan.FromMilliseconds(10));

        await using (var a = SluiceNode.Clustered(l2, _bus, Recovery))
        {
            await View(a.Cache);

            l2.Failing = true;
            await a.Cache.InvalidateAsync(_rows.For(1));
            l2.Failed.Should().NotBeEmpty("the invalidation's tag writes reached the failing L2");

            l2.Failing = false;
            await l2.Recovered.WaitAsync(TimeSpan.FromSeconds(10));
        }

        // A fresh node knows nothing but what the L2 holds: only the rewritten tag evicts the old entry.
        _data[1] = "new";
        await using var b = SluiceNode.Clustered(l2, _bus);
        (await View(b.Cache)).Should().Be("new");
        _runs.Should().Be(2);
    }

    [Theory]
    [InlineData("*", false)]
    [InlineData("!", false)]
    [InlineData("-rem", true)]
    [InlineData("-exp", true)]
    public async Task A_source_named_like_a_clear_sentinel_clears_nothing_on_other_nodes(
        string name,
        bool safeStrings
    )
    {
        void Strings(IFusionCacheBuilder cache)
        {
            if (safeStrings)
            {
                cache.WithOptions(o => o.InternalStrings.SetToSafeStrings());
            }
        }

        await using var a = SluiceNode.Clustered(_l2, _bus, Strings);
        await using var b = SluiceNode.Clustered(_l2, _bus, Strings);
        await View(b.Cache);
        var sentinel = new Source<int>(name);

        await a.Cache.InvalidateAsync([sentinel.All, sentinel.For(1)]);
        await View(b.Cache);

        _runs.Should().Be(1);
    }

    /// <summary>
    /// An L2 whose writes throw while <see cref="Failing"/>, as Redis does once its client times out. It completes
    /// <see cref="Recovered"/> when every key whose write failed has since been written.
    /// </summary>
    private sealed class FailingWrites : IDistributedCache
    {
        private readonly MemoryDistributedCache _inner = new(
            Options.Create(new MemoryDistributedCacheOptions())
        );
        private readonly HashSet<string> _unwritten = [];

        public volatile bool Failing;

        public HashSet<string> Failed { get; } = [];

        private readonly TaskCompletionSource _recovered = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task Recovered => _recovered.Task;

        public byte[]? Get(string key) => _inner.Get(key);

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
            _inner.GetAsync(key, token);

        public void Refresh(string key) => _inner.Refresh(key);

        public Task RefreshAsync(string key, CancellationToken token = default) =>
            _inner.RefreshAsync(key, token);

        public void Remove(string key) => _inner.Remove(key);

        public Task RemoveAsync(string key, CancellationToken token = default) =>
            _inner.RemoveAsync(key, token);

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            Write(key);
            _inner.Set(key, value, options);
            Written(key);
        }

        public async Task SetAsync(
            string key,
            byte[] value,
            DistributedCacheEntryOptions options,
            CancellationToken token = default
        )
        {
            Write(key);
            await _inner.SetAsync(key, value, options, token);
            Written(key);
        }

        private void Write(string key)
        {
            lock (_unwritten)
            {
                if (Failing)
                {
                    Failed.Add(key);
                    _unwritten.Add(key);
                    throw new TimeoutException("Simulated Redis client timeout.");
                }
            }
        }

        private void Written(string key)
        {
            lock (_unwritten)
            {
                if (_unwritten.Remove(key) && _unwritten.Count == 0)
                {
                    _recovered.TrySetResult();
                }
            }
        }
    }
}
