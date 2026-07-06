namespace Sluice.Tests;

public sealed class StampedeTests
{
    [Fact]
    public async Task Stampede_DeduplicatesCompute()
    {
        var store = new FakeStore();
        var gatedStore = new GatedCustomerStore(store);
        var queries = new OverlayQueries(gatedStore);
        var cacheStore = new InMemoryCacheStore();
        var sluice = new SluiceKernel(cacheStore);

        var customerId = new CustomerId("A");

        gatedStore.ArmGate();

        var leader = Task.Run(async () =>
            await sluice.Get(queries.CustomerScore, customerId, CancellationToken.None)
        );

        await gatedStore.GetCustomerEntered;

        var followers = new List<Task<CustomerScore>>();
        for (int i = 0; i < 9; i++)
        {
            followers.Add(
                Task.Run(async () =>
                    await sluice.Get(queries.CustomerScore, customerId, CancellationToken.None)
                )
            );
        }

        await Task.Delay(100);
        store.GetCustomerCallCount.Should().Be(0);

        gatedStore.ReleaseGate();

        var leaderResult = await leader;
        leaderResult.Score.Should().Be(20);

        var followerResults = await Task.WhenAll(followers);
        foreach (var r in followerResults)
        {
            r.Score.Should().Be(20);
        }

        store.GetCustomerCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Stampede_Failure_RetriesWithoutCachingException()
    {
        var store = new FakeStore();
        var failingStore = new FailingCustomerStore(store);
        var queries = new OverlayQueries(failingStore);
        var cacheStore = new InMemoryCacheStore();
        var sluice = new SluiceKernel(cacheStore);

        var customerId = new CustomerId("A");

        var firstException = await Record.ExceptionAsync(async () =>
            await sluice.Get(queries.CustomerScore, customerId, CancellationToken.None)
        );
        firstException.Should().NotBeNull();
        store.GetCustomerCallCount.Should().Be(1);

        var secondResult = await sluice.Get(
            queries.CustomerScore,
            customerId,
            CancellationToken.None
        );
        secondResult.Score.Should().Be(20);
        store.GetCustomerCallCount.Should().Be(2);
    }

    [Fact]
    public async Task Stampede_DifferentKeys_Parallel()
    {
        var store = new FakeStore();
        var gatedStore = new KeyedGatedCustomerStore(store, new CustomerId("A"));
        var queries = new OverlayQueries(gatedStore);
        var cacheStore = new InMemoryCacheStore();
        var sluice = new SluiceKernel(cacheStore);

        var customerA = new CustomerId("A");
        var customerB = new CustomerId("B");

        await sluice.Get(queries.CustomerScore, customerA, CancellationToken.None);
        await sluice.Get(queries.CustomerScore, customerB, CancellationToken.None);
        var entryKeyA = queries.CustomerScore.Operation.BuildEntryKey(customerA);
        var entryKeyB = queries.CustomerScore.Operation.BuildEntryKey(customerB);
        await cacheStore.RemoveAsync(entryKeyA, CancellationToken.None);
        await cacheStore.RemoveAsync(entryKeyB, CancellationToken.None);

        gatedStore.ArmGate();

        var taskA = Task.Run(async () =>
            await sluice.Get(queries.CustomerScore, customerA, CancellationToken.None)
        );

        await gatedStore.GetCustomerEntered;

        var taskB = Task.Run(async () =>
            await sluice.Get(queries.CustomerScore, customerB, CancellationToken.None)
        );

        var resultB = await taskB;
        resultB.Score.Should().Be(30);

        gatedStore.ReleaseGate();
        var resultA = await taskA;
        resultA.Score.Should().Be(20);
    }

    [Fact]
    public async Task Stampede_TtlExpired_SingleRecompute()
    {
        var store = new FakeStore();
        var gatedStore = new GatedCustomerStore(store);
        var queries = new OverlayQueries(gatedStore);
        var cacheStore = new InMemoryCacheStore();
        var sluice = new SluiceKernel(cacheStore);

        var customerId = new CustomerId("A");

        var customerScoreTtl = new CachedQuery<CustomerId, CustomerScore>(
            "customer.score",
            id => new { customerId = id.Value },
            async (id, read) =>
            {
                _ = await read.Track(
                    CustomerResources.Customer.For(id),
                    _ => gatedStore.GetCustomer(id)
                );
                var orders = await read.Track(
                    OrderResources.OrdersByCustomer.For(id),
                    _ => gatedStore.GetOrdersByCustomer(id)
                );
                return new CustomerScore(id, (int)orders.Sum(o => o.Total));
            },
            ttl: TimeSpan.FromMilliseconds(100)
        );

        var result1 = await sluice.Get(customerScoreTtl, customerId, CancellationToken.None);
        result1.Score.Should().Be(20);
        store.GetCustomerCallCount.Should().Be(1);

        await Task.Delay(150);

        gatedStore.ArmGate();

        var leader = Task.Run(async () =>
            await sluice.Get(queries.CustomerScore, customerId, CancellationToken.None)
        );

        await gatedStore.GetCustomerEntered;

        var followers = new List<Task<CustomerScore>>();
        for (int i = 0; i < 9; i++)
        {
            followers.Add(
                Task.Run(async () =>
                    await sluice.Get(queries.CustomerScore, customerId, CancellationToken.None)
                )
            );
        }

        await Task.Delay(100);
        store.GetCustomerCallCount.Should().Be(1);

        gatedStore.ReleaseGate();

        var leaderResult = await leader;
        leaderResult.Score.Should().Be(20);

        var followerResults = await Task.WhenAll(followers);
        foreach (var r in followerResults)
        {
            r.Score.Should().Be(20);
        }

        store.GetCustomerCallCount.Should().Be(2);
    }

    [Fact]
    public async Task Stampede_CancelledWhileWaiting_DoesNotDeadlock()
    {
        var store = new FakeStore();
        var gatedStore = new GatedCustomerStore(store);
        var queries = new OverlayQueries(gatedStore);
        var cacheStore = new InMemoryCacheStore();
        var sluice = new SluiceKernel(cacheStore);

        var customerId = new CustomerId("A");

        gatedStore.ArmGate();

        var leader = Task.Run(async () =>
            await sluice.Get(queries.CustomerScore, customerId, CancellationToken.None)
        );

        await gatedStore.GetCustomerEntered;

        var cts = new CancellationTokenSource();
        var follower = Task.Run(async () =>
        {
            cts.CancelAfter(50);
            try
            {
                await sluice.Get(queries.CustomerScore, customerId, cts.Token);
                return null;
            }
            catch (OperationCanceledException)
            {
                return "cancelled";
            }
        });

        var followerResult = await follower;
        followerResult.Should().Be("cancelled");

        gatedStore.ReleaseGate();
        var leaderResult = await leader;
        leaderResult.Score.Should().Be(20);
        store.GetCustomerCallCount.Should().Be(1);

        var freshResult = await sluice.Get(
            queries.CustomerScore,
            customerId,
            CancellationToken.None
        );
        freshResult.Score.Should().Be(20);
    }

    [Fact]
    public async Task Stampede_FlushAllAsync_WhileComputeLive_DoesNotDeadlock()
    {
        var store = new FakeStore();
        var gatedStore = new GatedCustomerStore(store);
        var queries = new OverlayQueries(gatedStore);
        var cacheStore = new InMemoryCacheStore();
        var sluice = new SluiceKernel(cacheStore);

        var customerId = new CustomerId("A");

        gatedStore.ArmGate();

        var compute = Task.Run(async () =>
            await sluice.Get(queries.CustomerScore, customerId, CancellationToken.None)
        );

        await gatedStore.GetCustomerEntered;

        await sluice.FlushAllAsync(CancellationToken.None);

        gatedStore.ReleaseGate();

        var result = await compute;
        result.Score.Should().Be(20);

        var freshResult = await sluice.Get(
            queries.CustomerScore,
            customerId,
            CancellationToken.None
        );
        freshResult.Score.Should().Be(20);
    }

    [Fact]
    public async Task LostInvalidation_WriteDuringCompute_SelfInvalidates()
    {
        var store = new FakeStore();
        var gatedStore = new GatedCustomerStore(store);
        var queries = new OverlayQueries(gatedStore);
        var cacheStore = new InMemoryCacheStore();
        var registry = new OperationRegistry(cacheStore).Register(queries.CustomerScore.Operation);

        var customerId = new CustomerId("A");

        await registry.ExecuteAsync(
            queries.CustomerScore.Operation,
            customerId,
            CancellationToken.None
        );
        store.GetCustomerCallCount.Should().Be(1);
        store.GetOrdersByCustomerCallCount.Should().Be(1);

        var entryKey = queries.CustomerScore.Operation.BuildEntryKey(customerId);
        await cacheStore.RemoveAsync(entryKey, CancellationToken.None);

        gatedStore.ArmGate();

        var read = Task.Run(async () =>
            await registry.ExecuteAsync(
                queries.CustomerScore.Operation,
                customerId,
                CancellationToken.None
            )
        );

        await gatedStore.GetCustomerEntered;

        await registry.ApplyAsync(
            ctx =>
                ctx.Apply(
                    () => Task.CompletedTask,
                    new WriteEffect(OrderResources.OrdersByCustomer.For(customerId))
                ),
            CancellationToken.None
        );

        gatedStore.ReleaseGate();

        var result = await read;
        result.Score.Should().Be(20);

        var nextResult = await registry.ExecuteAsync(
            queries.CustomerScore.Operation,
            customerId,
            CancellationToken.None
        );

        nextResult.Score.Should().Be(20);
        store.GetCustomerCallCount.Should().Be(3);
    }

    [Fact]
    public async Task LostInvalidation_UnrelatedWrite_DoesNotSelfInvalidate()
    {
        var store = new FakeStore();
        var gatedStore = new GatedCustomerStore(store);
        var queries = new OverlayQueries(gatedStore);
        var cacheStore = new InMemoryCacheStore();
        var registry = new OperationRegistry(cacheStore).Register(queries.CustomerScore.Operation);

        var customerA = new CustomerId("A");
        var customerB = new CustomerId("B");

        await registry.ExecuteAsync(
            queries.CustomerScore.Operation,
            customerA,
            CancellationToken.None
        );
        store.GetCustomerCallCount.Should().Be(1);

        var entryKey = queries.CustomerScore.Operation.BuildEntryKey(customerA);
        await cacheStore.RemoveAsync(entryKey, CancellationToken.None);

        gatedStore.ArmGate();

        var read = Task.Run(async () =>
            await registry.ExecuteAsync(
                queries.CustomerScore.Operation,
                customerA,
                CancellationToken.None
            )
        );

        await gatedStore.GetCustomerEntered;

        await registry.ApplyAsync(
            ctx =>
                ctx.Apply(
                    () => Task.CompletedTask,
                    new WriteEffect(OrderResources.OrdersByCustomer.For(customerB))
                ),
            CancellationToken.None
        );

        gatedStore.ReleaseGate();

        var result = await read;
        result.Score.Should().Be(20);

        var nextResult = await registry.ExecuteAsync(
            queries.CustomerScore.Operation,
            customerA,
            CancellationToken.None
        );

        nextResult.Score.Should().Be(20);
        store.GetCustomerCallCount.Should().Be(2);
    }

    [Fact]
    public async Task LostInvalidation_BurstExceedsMax_SelfInvalidates()
    {
        var store = new FakeStore();
        var gatedStore = new GatedCustomerStore(store);
        var queries = new OverlayQueries(gatedStore);
        var cacheStore = new InMemoryCacheStore();
        var registry = new OperationRegistry(cacheStore).Register(queries.CustomerScore.Operation);

        var customerId = new CustomerId("A");

        await registry.ExecuteAsync(
            queries.CustomerScore.Operation,
            customerId,
            CancellationToken.None
        );
        store.GetCustomerCallCount.Should().Be(1);

        var entryKey = queries.CustomerScore.Operation.BuildEntryKey(customerId);
        await cacheStore.RemoveAsync(entryKey, CancellationToken.None);

        gatedStore.ArmGate();

        var read = Task.Run(async () =>
            await registry.ExecuteAsync(
                queries.CustomerScore.Operation,
                customerId,
                CancellationToken.None
            )
        );

        await gatedStore.GetCustomerEntered;

        for (int i = 0; i < 300; i++)
        {
            var burstId = new CustomerId($"burst-{i}");
            await registry.ApplyAsync(
                ctx =>
                    ctx.Apply(
                        () => Task.CompletedTask,
                        new WriteEffect(OrderResources.OrdersByCustomer.For(burstId))
                    ),
                CancellationToken.None
            );
        }

        gatedStore.ReleaseGate();

        var result = await read;
        result.Score.Should().Be(20);

        var nextResult = await registry.ExecuteAsync(
            queries.CustomerScore.Operation,
            customerId,
            CancellationToken.None
        );

        nextResult.Score.Should().Be(20);
        store.GetCustomerCallCount.Should().Be(3);
    }

    [Fact]
    public async Task LostInvalidation_WildcardWriteDuringCompute_SelfInvalidates()
    {
        var store = new FakeStore();
        var gatedStore = new GatedCustomerStore(store);
        var queries = new OverlayQueries(gatedStore);
        var cacheStore = new InMemoryCacheStore();
        var registry = new OperationRegistry(cacheStore).Register(queries.CustomerScore.Operation);

        var customerId = new CustomerId("A");

        var result1 = await registry.ExecuteAsync(
            queries.CustomerScore.Operation,
            customerId,
            CancellationToken.None
        );
        result1.Score.Should().Be(20);
        store.GetCustomerCallCount.Should().Be(1);

        var entryKey = queries.CustomerScore.Operation.BuildEntryKey(customerId);
        await cacheStore.RemoveAsync(entryKey, CancellationToken.None);

        gatedStore.ArmGate();

        var read = Task.Run(async () =>
            await registry.ExecuteAsync(
                queries.CustomerScore.Operation,
                customerId,
                CancellationToken.None
            )
        );

        await gatedStore.GetCustomerEntered;

        await registry.ApplyAsync(
            ctx =>
                ctx.Apply(
                    () => Task.CompletedTask,
                    new WriteEffect(CustomerResources.Customer.Wildcard())
                ),
            CancellationToken.None
        );

        gatedStore.ReleaseGate();

        var result = await read;
        result.Score.Should().Be(20);

        var nextResult = await registry.ExecuteAsync(
            queries.CustomerScore.Operation,
            customerId,
            CancellationToken.None
        );

        nextResult.Score.Should().Be(20);
        store.GetCustomerCallCount.Should().Be(3);
    }

    [Fact]
    public async Task CacheEntry_Deserializes_OldJson_WithoutWriteEpoch()
    {
        var oldJson = """
            {
              "Value": {"CustomerId": {"Value":"A"}, "Score":20},
              "ObservedReads": [
                {"Kind":1,"Name":"customer","Key":"A"},
                {"Kind":2,"Name":"orders.byCustomer","Key":"A"}
              ],
              "CachedAt": "2026-01-01T00:00:00.0000000Z",
              "ExpiresAt": "2026-01-01T01:00:00.0000000Z"
            }
            """;

        var entry = System.Text.Json.JsonSerializer.Deserialize<CacheEntry<CustomerScore>>(oldJson);

        entry.Should().NotBeNull();
        entry!.Value.CustomerId.Value.Should().Be("A");
        entry.Value.Score.Should().Be(20);
        entry.WriteEpoch.Should().Be(0);
    }

    private sealed class GatedCustomerStore(IStore inner) : IStore
    {
        private TaskCompletionSource<bool>? _getCustomerEntered;
        private TaskCompletionSource<bool>? _getCustomerRelease;

        public Task GetCustomerEntered => _getCustomerEntered?.Task ?? Task.CompletedTask;

        public void ArmGate()
        {
            _getCustomerEntered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _getCustomerRelease = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
        }

        public void ReleaseGate() => _getCustomerRelease?.TrySetResult(true);

        public async Task<Customer> GetCustomer(CustomerId id)
        {
            _getCustomerEntered?.TrySetResult(true);
            var release = _getCustomerRelease;
            if (release is not null)
            {
                await release.Task;
            }
            return await inner.GetCustomer(id);
        }

        public Task<IReadOnlyList<Order>> GetOrdersByCustomer(CustomerId id) =>
            inner.GetOrdersByCustomer(id);

        public Task UpdateCustomer(CustomerId id, CustomerPatch patch) =>
            inner.UpdateCustomer(id, patch);

        public Task<Order> CreateOrder(CustomerId customerId, CreateOrderInput input) =>
            inner.CreateOrder(customerId, input);

        public Task<Order> GetOrder(OrderId orderId) => inner.GetOrder(orderId);

        public Task DeleteOrder(OrderId orderId) => inner.DeleteOrder(orderId);

        public Task ReassignOrder(OrderId orderId, CustomerId newCustomerId) =>
            inner.ReassignOrder(orderId, newCustomerId);
    }

    private sealed class KeyedGatedCustomerStore(IStore inner, CustomerId gateId) : IStore
    {
        private TaskCompletionSource<bool>? _getCustomerEntered;
        private TaskCompletionSource<bool>? _getCustomerRelease;

        public Task GetCustomerEntered => _getCustomerEntered?.Task ?? Task.CompletedTask;

        public void ArmGate()
        {
            _getCustomerEntered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _getCustomerRelease = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
        }

        public void ReleaseGate() => _getCustomerRelease?.TrySetResult(true);

        public async Task<Customer> GetCustomer(CustomerId id)
        {
            if (id == gateId)
            {
                _getCustomerEntered?.TrySetResult(true);
                var release = _getCustomerRelease;
                if (release is not null)
                {
                    await release.Task;
                }
            }
            return await inner.GetCustomer(id);
        }

        public Task<IReadOnlyList<Order>> GetOrdersByCustomer(CustomerId id) =>
            inner.GetOrdersByCustomer(id);

        public Task UpdateCustomer(CustomerId id, CustomerPatch patch) =>
            inner.UpdateCustomer(id, patch);

        public Task<Order> CreateOrder(CustomerId customerId, CreateOrderInput input) =>
            inner.CreateOrder(customerId, input);

        public Task<Order> GetOrder(OrderId orderId) => inner.GetOrder(orderId);

        public Task DeleteOrder(OrderId orderId) => inner.DeleteOrder(orderId);

        public Task ReassignOrder(OrderId orderId, CustomerId newCustomerId) =>
            inner.ReassignOrder(orderId, newCustomerId);
    }

    private sealed class FailingCustomerStore(IStore inner) : IStore
    {
        private bool _hasFailed;

        public async Task<Customer> GetCustomer(CustomerId id)
        {
            if (!_hasFailed)
            {
                _hasFailed = true;
                await inner.GetCustomer(id);
                throw new InvalidOperationException("Simulated failure");
            }
            return await inner.GetCustomer(id);
        }

        public Task<IReadOnlyList<Order>> GetOrdersByCustomer(CustomerId id) =>
            inner.GetOrdersByCustomer(id);

        public Task UpdateCustomer(CustomerId id, CustomerPatch patch) =>
            inner.UpdateCustomer(id, patch);

        public Task<Order> CreateOrder(CustomerId customerId, CreateOrderInput input) =>
            inner.CreateOrder(customerId, input);

        public Task<Order> GetOrder(OrderId orderId) => inner.GetOrder(orderId);

        public Task DeleteOrder(OrderId orderId) => inner.DeleteOrder(orderId);

        public Task ReassignOrder(OrderId orderId, CustomerId newCustomerId) =>
            inner.ReassignOrder(orderId, newCustomerId);
    }
}
