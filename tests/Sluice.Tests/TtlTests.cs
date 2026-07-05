namespace Sluice.Tests;

public sealed class TtlTests
{
    private sealed class TrackingCacheStore(ICacheStore inner) : ICacheStore
    {
        public int RemoveCallCount { get; private set; }

        public string? LastRemovedKey { get; private set; }

        private readonly TaskCompletionSource _removed = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        private TaskCompletionSource? _setGate;

        public Task Removed => _removed.Task;

        public void BlockNextSet() =>
            _setGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseSet()
        {
            _setGate?.TrySetResult();
            _setGate = null;
        }

        public Task<CacheEntry<TValue>?> GetAsync<TValue>(string key, CancellationToken ct) =>
            inner.GetAsync<TValue>(key, ct);

        public Task SetAsync<TValue>(string key, CacheEntry<TValue> entry, CancellationToken ct)
        {
            var setGate = _setGate;
            if (setGate is not null)
            {
                return SetAfterGateAsync(key, entry, ct, setGate.Task);
            }

            return inner.SetAsync(key, entry, ct);
        }

        public async Task<bool> RemoveAsync(string key, CancellationToken ct)
        {
            RemoveCallCount++;
            LastRemovedKey = key;
            _removed.TrySetResult();
            return await inner.RemoveAsync(key, ct);
        }

        public Task ClearAsync(CancellationToken ct) => inner.ClearAsync(ct);

        private async Task SetAfterGateAsync<TValue>(
            string key,
            CacheEntry<TValue> entry,
            CancellationToken ct,
            Task gate
        )
        {
            await gate;
            await inner.SetAsync(key, entry, ct);
        }
    }

    private sealed class FakeClock(DateTimeOffset? start = null) : TimeProvider
    {
        private DateTimeOffset _now = start ?? DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }

    private static SluiceKernel CreateKernel(ICacheStore cacheStore, TimeProvider? clock = null) =>
        new(cacheStore, clock: clock);

    private static Query<CustomerId, CustomerScore> ScoreQuery(
        IStore store,
        TimeSpan? ttl = null
    ) =>
        new(
            "customer.score",
            id => new { customerId = id.Value },
            async (id, read) =>
            {
                _ = await read.Track(
                    CustomerResources.Customer.For(id),
                    _ => store.GetCustomer(id)
                );
                var orders = await read.Track(
                    OrderResources.OrdersByCustomer.For(id),
                    _ => store.GetOrdersByCustomer(id)
                );
                return new CustomerScore(id, (int)orders.Sum(o => o.Total));
            },
            ttl: ttl
        );

    [Fact]
    public async Task Ttl_EntryExpires_AfterTtl()
    {
        var store = new FakeStore();
        var query = ScoreQuery(store, TimeSpan.FromMilliseconds(100));
        var cacheStore = new TrackingCacheStore(new InMemoryCacheStore());
        var clock = new FakeClock();
        var sluice = CreateKernel(cacheStore, clock);

        var customerA = new CustomerId("A");

        var result1 = await sluice.Get(query, customerA, CancellationToken.None);
        result1.Score.Should().Be(20);
        store.GetCustomerCallCount.Should().Be(1);

        var entryKey = "customer.score:v1:{\"customerId\":\"A\"}";
        var entryBefore = await cacheStore.GetAsync<CustomerScore>(
            entryKey,
            CancellationToken.None
        );
        entryBefore.Should().NotBeNull();
        entryBefore!.ExpiresAt.Should().BeAfter(clock.GetUtcNow());
        var cachedAtBefore = entryBefore.CachedAt;

        clock.Advance(TimeSpan.FromMilliseconds(150));

        var result2 = await sluice.Get(query, customerA, CancellationToken.None);
        result2.Score.Should().Be(20);
        store.GetCustomerCallCount.Should().Be(2);
        cacheStore.RemoveCallCount.Should().Be(1);

        var entryAfter = await cacheStore.GetAsync<CustomerScore>(entryKey, CancellationToken.None);
        entryAfter.Should().NotBeNull();
        entryAfter!.CachedAt.Should().BeAfter(cachedAtBefore);
        entryAfter.ExpiresAt.Should().BeAfter(clock.GetUtcNow());
    }

    [Fact]
    public async Task Ttl_NoTtl_NeverExpires()
    {
        var store = new FakeStore();
        var query = ScoreQuery(store);
        var cacheStore = new TrackingCacheStore(new InMemoryCacheStore());
        var sluice = CreateKernel(cacheStore);

        var customerA = new CustomerId("A");

        await sluice.Get(query, customerA, CancellationToken.None);
        store.GetCustomerCallCount.Should().Be(1);
        cacheStore.RemoveCallCount.Should().Be(0);

        await sluice.Get(query, customerA, CancellationToken.None);
        store.GetCustomerCallCount.Should().Be(1);
        cacheStore.RemoveCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Ttl_ExpiredEntry_RemovedFromCache()
    {
        var store = new FakeStore();
        var query = ScoreQuery(store, TimeSpan.FromMilliseconds(100));
        var cacheStore = new TrackingCacheStore(new InMemoryCacheStore());
        var clock = new FakeClock();
        var sluice = CreateKernel(cacheStore, clock);

        var customerA = new CustomerId("A");

        await sluice.Get(query, customerA, CancellationToken.None);
        store.GetCustomerCallCount.Should().Be(1);

        var entryKey = "customer.score:v1:{\"customerId\":\"A\"}";
        var entryBefore = await cacheStore.GetAsync<CustomerScore>(
            entryKey,
            CancellationToken.None
        );
        entryBefore.Should().NotBeNull();
        entryBefore!.ExpiresAt.Should().NotBeNull();
        entryBefore.ExpiresAt.Should().BeAfter(clock.GetUtcNow());

        clock.Advance(TimeSpan.FromMilliseconds(150));

        cacheStore.BlockNextSet();

        var refreshTask = sluice.Get(query, customerA, CancellationToken.None);
        await cacheStore.Removed;

        cacheStore.RemoveCallCount.Should().Be(1);
        cacheStore.LastRemovedKey.Should().Be(entryKey);

        var removedEntry = await cacheStore.GetAsync<CustomerScore>(
            entryKey,
            CancellationToken.None
        );
        removedEntry.Should().BeNull();

        cacheStore.ReleaseSet();
        await refreshTask;
        store.GetCustomerCallCount.Should().Be(2);

        var entryAfter = await cacheStore.GetAsync<CustomerScore>(entryKey, CancellationToken.None);
        entryAfter.Should().NotBeNull();
        entryAfter.ExpiresAt.Should().NotBeNull();
        entryAfter.ExpiresAt.Should().BeAfter(clock.GetUtcNow());
    }

    [Fact]
    public async Task Ttl_ExpiredEntry_TriggersGraphReplacement()
    {
        var store = new FakeStore();
        var query = ScoreQuery(store, TimeSpan.FromMilliseconds(100));
        var cacheStore = new TrackingCacheStore(new InMemoryCacheStore());
        var clock = new FakeClock();
        var sluice = CreateKernel(cacheStore, clock);

        var customerA = new CustomerId("A");

        await sluice.Get(query, customerA, CancellationToken.None);
        store.GetCustomerCallCount.Should().Be(1);

        var graphBefore = await sluice.DumpGraphAsync(CancellationToken.None);
        graphBefore.Should().Contain("customer.score:v1:{\"customerId\":\"A\"}");
        graphBefore.Should().Contain("entity:customer:A");
        graphBefore.Should().Contain("collection:orders.byCustomer:A");
        graphBefore.Should().Contain("cached:");

        clock.Advance(TimeSpan.FromMilliseconds(150));

        cacheStore.BlockNextSet();

        var refreshTask = sluice.Get(query, customerA, CancellationToken.None);
        await cacheStore.Removed;

        cacheStore.RemoveCallCount.Should().Be(1);

        var graphDuringRefresh = await sluice.DumpGraphAsync(CancellationToken.None);
        graphDuringRefresh.Should().NotContain("customer.score:v1:{\"customerId\":\"A\"}");
        graphDuringRefresh.Should().NotContain("entity:customer:A");
        graphDuringRefresh.Should().NotContain("collection:orders.byCustomer:A");

        cacheStore.ReleaseSet();
        await refreshTask;
        store.GetCustomerCallCount.Should().Be(2);

        var graphAfter = await sluice.DumpGraphAsync(CancellationToken.None);
        graphAfter.Should().Contain("customer.score:v1:{\"customerId\":\"A\"}");
        graphAfter.Should().Contain("entity:customer:A");
        graphAfter.Should().Contain("collection:orders.byCustomer:A");
        graphAfter.Should().Contain("cached:");
        graphAfter.Should().NotBe(graphBefore);
    }

    [Fact]
    public async Task Ttl_NotYetExpired_IsCacheHit()
    {
        var store = new FakeStore();
        var query = ScoreQuery(store, TimeSpan.FromMinutes(5));
        var cacheStore = new TrackingCacheStore(new InMemoryCacheStore());
        var sluice = CreateKernel(cacheStore);

        var customerA = new CustomerId("A");

        await sluice.Get(query, customerA, CancellationToken.None);
        store.GetCustomerCallCount.Should().Be(1);
        cacheStore.RemoveCallCount.Should().Be(0);

        await sluice.Get(query, customerA, CancellationToken.None);
        store.GetCustomerCallCount.Should().Be(1);
        cacheStore.RemoveCallCount.Should().Be(0);
    }
}
