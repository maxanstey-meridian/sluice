namespace Sluice.Tests;

public sealed class SafetyGuardTests
{
    public sealed class KeyValidation
    {
        [Fact]
        public void EntityResource_For_Rejects_EmptyKey()
        {
            var resource = new EntityResource<CustomerId>("customer");
            var act = () => resource.For(new CustomerId(""));
            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void EntityResource_For_Rejects_WhitespaceKey()
        {
            var resource = new EntityResource<CustomerId>("customer");
            var act = () => resource.For(new CustomerId("  "));
            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void EntityResource_For_Rejects_ColonInKey()
        {
            var resource = new EntityResource<CustomerId>("customer");
            var act = () => resource.For(new CustomerId("tenant:alice"));
            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void EntityResource_For_Rejects_WildcardKey()
        {
            var resource = new EntityResource<CustomerId>("customer");
            var act = () => resource.For(new CustomerId("*"));
            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void CollectionResource_For_Rejects_WildcardKey()
        {
            var resource = new CollectionResource<CustomerId>("orders.byCustomer");
            var act = () => resource.For(new CustomerId("*"));
            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Wildcard_Is_NotAffected_By_Validation()
        {
            var entity = new EntityResource<CustomerId>("customer");
            var collection = new CollectionResource<CustomerId>("orders.byCustomer");

            entity.Wildcard().Key.Should().Be("*");
            collection.Wildcard().Key.Should().Be("*");
        }
    }

    public sealed class UntrackedGuard
    {
        [Fact]
        public async Task Untracked_Operation_Throws_InvalidOperationException()
        {
            var cacheStore = new InMemoryCacheStore();
            var registry = new OperationRegistry(cacheStore);

            var query = new CachedQuery<CustomerId, CustomerScore>(
                "untracked.score",
                (_, _) => ValueTask.FromResult(new CustomerScore(new CustomerId("A"), 42))
            );

            var act = async () =>
                await registry.ExecuteAsync(
                    query.Operation,
                    new CustomerId("A"),
                    CancellationToken.None
                );

            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        [Fact]
        public async Task AllowUntracked_True_Succeeds_And_Caches()
        {
            var computeCount = 0;
            var cacheStore = new InMemoryCacheStore();
            var registry = new OperationRegistry(cacheStore);

            var query = new CachedQuery<CustomerId, CustomerScore>(
                "allowed.untracked",
                (_, _) =>
                {
                    computeCount++;
                    return ValueTask.FromResult(new CustomerScore(new CustomerId("A"), 42));
                },
                allowUntracked: true
            );

            var result1 = await registry.ExecuteAsync(
                query.Operation,
                new CustomerId("A"),
                CancellationToken.None
            );
            result1.Score.Should().Be(42);
            computeCount.Should().Be(1);

            var result2 = await registry.ExecuteAsync(
                query.Operation,
                new CustomerId("A"),
                CancellationToken.None
            );
            result2.Score.Should().Be(42);
            computeCount.Should().Be(1);
        }

        [Fact]
        public async Task Guard_Leaves_NoPartial_State()
        {
            var cacheStore = new InMemoryCacheStore();
            var registry = new OperationRegistry(cacheStore);

            var query = new CachedQuery<CustomerId, CustomerScore>(
                "untracked.score",
                (_, _) => ValueTask.FromResult(new CustomerScore(new CustomerId("A"), 42))
            );

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                registry.ExecuteAsync(query.Operation, new CustomerId("A"), CancellationToken.None)
            );

            var graph = await registry.DumpGraphAsync(CancellationToken.None);
            graph.Should().NotContain("untracked.score");
        }
    }

    public sealed class WriteOrdering
    {
        private sealed class BlockingCacheStore : ICacheStore
        {
            private readonly ICacheStore _inner = new InMemoryCacheStore();
            private TaskCompletionSource? _setGate;
            private readonly TaskCompletionSource _setEntered = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );

            public Task SetEntered => _setEntered.Task;

            public void BlockNextSet() =>
                _setGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void ReleaseSet()
            {
                _setGate?.TrySetResult();
                _setGate = null;
            }

            public Task<CacheEntry<TValue>?> GetAsync<TValue>(string key, CancellationToken ct) =>
                _inner.GetAsync<TValue>(key, ct);

            public async Task SetAsync<TValue>(
                string key,
                CacheEntry<TValue> entry,
                CancellationToken ct
            )
            {
                _setEntered.TrySetResult();
                var gate = _setGate;
                if (gate is not null)
                {
                    await gate.Task;
                }
                await _inner.SetAsync(key, entry, ct);
            }

            public Task<bool> RemoveAsync(string key, CancellationToken ct) =>
                _inner.RemoveAsync(key, ct);

            public Task ClearAsync(CancellationToken ct) => _inner.ClearAsync(ct);
        }

        [Fact]
        public async Task RecordEntry_Runs_Before_SetAsync()
        {
            var store = new FakeStore();
            var customers = new TrackedCustomers(store);
            var orders = new TrackedOrders(store);
            var operation = new CustomerScoreOperation(customers, orders);
            var cacheStore = new BlockingCacheStore();
            var registry = new OperationRegistry(cacheStore).Register(operation);

            var customerA = new CustomerId("A");
            var entryKey = operation.BuildEntryKey(customerA);

            cacheStore.BlockNextSet();

            var task = registry.ExecuteAsync(operation, customerA, CancellationToken.None);
            await cacheStore.SetEntered;

            var graph = await registry.DumpGraphAsync(CancellationToken.None);
            graph.Should().Contain(entryKey);
            graph.Should().Contain("entity:customer:A");

            var cached = await cacheStore.GetAsync<CustomerScore>(entryKey, CancellationToken.None);
            cached.Should().BeNull();

            cacheStore.ReleaseSet();
            await task;

            var cachedAfter = await cacheStore.GetAsync<CustomerScore>(
                entryKey,
                CancellationToken.None
            );
            cachedAfter.Should().NotBeNull();
            cachedAfter!.Value.Score.Should().Be(20);
        }
    }
}
