namespace Sluice.Tests;

public sealed class ThreadSafetyTests
{
    [Fact]
    public async Task Concurrent_ReadsAndWrites_DoNotCorruptIndexes()
    {
        var store = new FakeStore();
        var controlledStore = new ControlledStore(store);
        var customers = new TrackedCustomers(controlledStore);
        var orders = new TrackedOrders(controlledStore);
        var operation = new CustomerScoreOperation(customers, orders);
        var cacheStore = new InMemoryCacheStore();
        var registry = new OperationRegistry(cacheStore).Register(operation);

        var customerIdA = new CustomerId("A");

        await registry.ExecuteAsync(operation, customerIdA, CancellationToken.None);

        var scoreA = await cacheStore.GetAsync<CustomerScore>(
            operation.BuildEntryKey(customerIdA),
            CancellationToken.None
        );
        var initialScoreA = scoreA!.Value.Score;

        await cacheStore.RemoveAsync(operation.BuildEntryKey(customerIdA), CancellationToken.None);

        controlledStore.ArmOrdersReadGate(expectedOrdersReadStarts: 1);

        var readTasks = new List<Task<int>>();
        for (int i = 0; i < 10; i++)
        {
            readTasks.Add(
                Task.Run(async () =>
                {
                    var result = await registry.ExecuteAsync(
                        operation,
                        customerIdA,
                        CancellationToken.None
                    );
                    return result.Score;
                })
            );
        }

        await controlledStore.OrdersReadEntered;

        var writeTasks = new List<Task>();
        for (int i = 0; i < 5; i++)
        {
            writeTasks.Add(
                Task.Run(async () =>
                {
                    await registry.ApplyAsync(
                        ctx => orders.Create(customerIdA, new CreateOrderInput(10m), ctx),
                        CancellationToken.None
                    );
                })
            );
        }

        await Task.WhenAll(writeTasks);

        controlledStore.ReleaseOrdersReadGate();

        var results = await Task.WhenAll(readTasks);

        var expectedA = initialScoreA + (5 * 10);

        foreach (var r in results)
        {
            r.Should().Be(expectedA);
        }

        var entryA = await cacheStore.GetAsync<CustomerScore>(
            operation.BuildEntryKey(customerIdA),
            CancellationToken.None
        );
        entryA.Should().NotBeNull();
        entryA!.Value.Score.Should().Be(expectedA);

        var graph = registry.DumpGraph();
        graph.Should().Contain("customer.score:v1:{\"customerId\":\"A\"}");
        graph.Should().Contain("entity:customer:A");
        graph.Should().Contain("collection:orders.byCustomer:A");
        graph.Should().Contain("cached:");
        graph.Should().Contain("invalidates:");
    }

    [Fact]
    public async Task Concurrent_InvalidateDuringCompute_PreservesConsistency()
    {
        var store = new FakeStore();
        var controlledStore = new ControlledStore(store);
        var customers = new TrackedCustomers(controlledStore);
        var orders = new TrackedOrders(controlledStore);
        var operation = new CustomerScoreOperation(customers, orders);
        var cacheStore = new InMemoryCacheStore();
        var registry = new OperationRegistry(cacheStore).Register(operation);

        var customerId = new CustomerId("A");
        var entryKey = operation.BuildEntryKey(customerId);

        await registry.ExecuteAsync(operation, customerId, CancellationToken.None);
        var initialScore = await cacheStore.GetAsync<CustomerScore>(
            entryKey,
            CancellationToken.None
        );

        await cacheStore.RemoveAsync(entryKey, CancellationToken.None);

        controlledStore.ArmOrdersReadGate(expectedOrdersReadStarts: 1);
        var writeGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var writeStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        var readTask = Task.Run(async () =>
            await registry.ExecuteAsync(operation, customerId, CancellationToken.None)
        );

        await controlledStore.OrdersReadEntered;

        var writeTask = Task.Run(async () =>
            await registry.ApplyAsync(
                async ctx =>
                {
                    writeStarted.TrySetResult(true);
                    await writeGate.Task;
                    await orders.Create(customerId, new CreateOrderInput(25m), ctx);
                },
                CancellationToken.None
            )
        );

        await writeStarted.Task;
        writeGate.TrySetResult(true);

        await writeTask;

        controlledStore.ReleaseOrdersReadGate();

        await readTask;

        var convergentResult = await registry.ExecuteAsync(
            operation,
            customerId,
            CancellationToken.None
        );
        convergentResult.Score.Should().Be(initialScore!.Value.Score + 25);
    }

    [Fact]
    public async Task Concurrent_WildcardInvalidation_SnapshotsSafely()
    {
        var store = new FakeStore();
        var controlledStore = new ControlledStore(store);
        var customers = new TrackedCustomers(controlledStore);
        var orders = new TrackedOrders(controlledStore);
        var operation = new CustomerScoreOperation(customers, orders);
        var cacheStore = new InMemoryCacheStore();
        var registry = new OperationRegistry(cacheStore).Register(operation);

        var customerIdA = new CustomerId("A");
        var customerIdB = new CustomerId("B");

        await registry.ExecuteAsync(operation, customerIdA, CancellationToken.None);
        await registry.ExecuteAsync(operation, customerIdB, CancellationToken.None);

        await cacheStore.RemoveAsync(operation.BuildEntryKey(customerIdA), CancellationToken.None);
        await cacheStore.RemoveAsync(operation.BuildEntryKey(customerIdB), CancellationToken.None);

        controlledStore.ArmOrdersReadGate(expectedOrdersReadStarts: 2);

        var exceptions = new List<Exception>();
        var lockObj = new object();

        void Catch(Exception ex)
        {
            lock (lockObj)
            {
                exceptions.Add(ex);
            }
        }

        var readTasks = new List<Task>();
        for (int i = 0; i < 10; i++)
        {
            var id = i % 2 == 0 ? customerIdA : customerIdB;
            readTasks.Add(
                Task.Run(async () =>
                {
                    try
                    {
                        await registry.ExecuteAsync(operation, id, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        Catch(ex);
                    }
                })
            );
        }

        await controlledStore.OrdersReadEntered;

        var wildcardTasks = new List<Task>();
        for (int i = 0; i < 3; i++)
        {
            wildcardTasks.Add(
                Task.Run(async () =>
                {
                    try
                    {
                        await registry.ApplyAsync(
                            ctx =>
                                ctx.Apply(
                                    () => Task.CompletedTask,
                                    new WriteEffect(CustomerResources.Customer.Wildcard())
                                ),
                            CancellationToken.None
                        );
                    }
                    catch (Exception ex)
                    {
                        Catch(ex);
                    }
                })
            );
        }

        await Task.WhenAll(wildcardTasks);

        controlledStore.ReleaseOrdersReadGate();

        await Task.WhenAll(readTasks);

        exceptions.Should().BeEmpty();

        var finalA = await registry.ExecuteAsync(operation, customerIdA, CancellationToken.None);
        finalA.Should().NotBeNull();
    }
}

internal sealed class ControlledStore(IStore inner) : IStore
{
    private TaskCompletionSource<bool>? _ordersReadEntered;
    private TaskCompletionSource<bool>? _ordersReadRelease;
    private int _ordersReadStarts;
    private int _expectedOrdersReadStarts;

    public Task OrdersReadEntered => _ordersReadEntered?.Task ?? Task.CompletedTask;

    public void ArmOrdersReadGate(int expectedOrdersReadStarts)
    {
        _ordersReadStarts = 0;
        _expectedOrdersReadStarts = expectedOrdersReadStarts;
        _ordersReadEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        _ordersReadRelease = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
    }

    public void ReleaseOrdersReadGate() => _ordersReadRelease?.TrySetResult(true);

    public Task<Customer> GetCustomer(CustomerId id) => inner.GetCustomer(id);

    public async Task<IReadOnlyList<Order>> GetOrdersByCustomer(CustomerId id)
    {
        var entered = _ordersReadEntered;
        var release = _ordersReadRelease;
        if (entered is not null && release is not null)
        {
            if (Interlocked.Increment(ref _ordersReadStarts) == _expectedOrdersReadStarts)
            {
                entered.TrySetResult(true);
            }

            await release.Task;
        }

        return await inner.GetOrdersByCustomer(id);
    }

    public Task UpdateCustomer(CustomerId id, CustomerPatch patch) =>
        inner.UpdateCustomer(id, patch);

    public Task<Order> CreateOrder(CustomerId customerId, CreateOrderInput input) =>
        inner.CreateOrder(customerId, input);

    public Task<Order> GetOrder(OrderId orderId) => inner.GetOrder(orderId);

    public Task DeleteOrder(OrderId orderId) => inner.DeleteOrder(orderId);

    public Task ReassignOrder(OrderId orderId, CustomerId newCustomerId) =>
        inner.ReassignOrder(orderId, newCustomerId);
}
