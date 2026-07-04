namespace Sluice.Tests;

public sealed class WritePathTests
{
    [Fact]
    public async Task Create_Invalidates_CustomerScore()
    {
        var store = new FakeStore();
        var customers = new TrackedCustomers(store);
        var orders = new TrackedOrders(store);
        var operation = new CustomerScoreOperation(customers, orders);
        var cacheStore = new InMemoryCacheStore();
        var registry = new OperationRegistry(cacheStore).Register(operation);

        var customerA = new CustomerId("A");

        await registry.ExecuteAsync(operation, customerA, CancellationToken.None);

        store.GetCustomerCallCount.Should().Be(1);
        store.GetOrdersByCustomerCallCount.Should().Be(1);

        await registry.ApplyAsync(
            ctx => orders.Create(customerA, new CreateOrderInput(10m), ctx),
            CancellationToken.None
        );

        var result = await registry.ExecuteAsync(operation, customerA, CancellationToken.None);

        result.Score.Should().Be(30);
        store.GetCustomerCallCount.Should().Be(2);
        store.GetOrdersByCustomerCallCount.Should().Be(2);
    }

    [Fact]
    public async Task Update_Invalidates_CustomerScore()
    {
        var store = new FakeStore();
        var customers = new TrackedCustomers(store);
        var orders = new TrackedOrders(store);
        var operation = new CustomerScoreOperation(customers, orders);
        var cacheStore = new InMemoryCacheStore();
        var registry = new OperationRegistry(cacheStore).Register(operation);

        var customerA = new CustomerId("A");

        await registry.ExecuteAsync(operation, customerA, CancellationToken.None);

        store.GetCustomerCallCount.Should().Be(1);
        store.GetOrdersByCustomerCallCount.Should().Be(1);

        await registry.ApplyAsync(
            ctx => customers.Update(customerA, new CustomerPatch("Updated", null), ctx),
            CancellationToken.None
        );

        var result = await registry.ExecuteAsync(operation, customerA, CancellationToken.None);

        result.Score.Should().Be(20);
        store.GetCustomerCallCount.Should().Be(2);
        store.GetOrdersByCustomerCallCount.Should().Be(2);
    }

    [Fact]
    public async Task Delete_Invalidates_Both_Entity_And_Collection()
    {
        var store = new FakeStore();
        var customers = new TrackedCustomers(store);
        var orders = new TrackedOrders(store);
        var operation = new CustomerScoreOperation(customers, orders);
        var cacheStore = new InMemoryCacheStore();
        var registry = new OperationRegistry(cacheStore).Register(operation);

        var customerA = new CustomerId("A");
        var order4 = new OrderId("o4");

        await registry.ExecuteAsync(operation, customerA, CancellationToken.None);

        store.GetCustomerCallCount.Should().Be(1);
        store.GetOrdersByCustomerCallCount.Should().Be(1);

        await registry.ApplyAsync(ctx => orders.Delete(order4, ctx), CancellationToken.None);

        var result = await registry.ExecuteAsync(operation, customerA, CancellationToken.None);

        result.Score.Should().Be(0);
        store.GetCustomerCallCount.Should().Be(2);
        store.GetOrdersByCustomerCallCount.Should().Be(2);
    }

    [Fact]
    public async Task Reassign_Invalidates_Old_And_New_Collections()
    {
        var store = new FakeStore();
        var customers = new TrackedCustomers(store);
        var orders = new TrackedOrders(store);
        var operation = new CustomerScoreOperation(customers, orders);
        var cacheStore = new InMemoryCacheStore();
        var registry = new OperationRegistry(cacheStore).Register(operation);

        var customerA = new CustomerId("A");
        var customerB = new CustomerId("B");
        var order4 = new OrderId("o4");

        await registry.ExecuteAsync(operation, customerA, CancellationToken.None);
        await registry.ExecuteAsync(operation, customerB, CancellationToken.None);

        store.GetCustomerCallCount.Should().Be(2);
        store.GetOrdersByCustomerCallCount.Should().Be(2);

        await registry.ApplyAsync(
            ctx => orders.Reassign(order4, customerB, ctx),
            CancellationToken.None
        );

        var resultA = await registry.ExecuteAsync(operation, customerA, CancellationToken.None);

        var resultB = await registry.ExecuteAsync(operation, customerB, CancellationToken.None);

        resultA.Score.Should().Be(0);
        resultB.Score.Should().Be(50);
        store.GetCustomerCallCount.Should().Be(4);
        store.GetOrdersByCustomerCallCount.Should().Be(4);
        store.GetOrderCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Wildcard_Invalidation_Evicts_All_Customer_Score_Entries()
    {
        var store = new FakeStore();
        var customers = new TrackedCustomers(store);
        var orders = new TrackedOrders(store);
        var operation = new CustomerScoreOperation(customers, orders);
        var cacheStore = new InMemoryCacheStore();
        var registry = new OperationRegistry(cacheStore).Register(operation);

        var customerA = new CustomerId("A");
        var customerB = new CustomerId("B");

        await registry.ExecuteAsync(operation, customerA, CancellationToken.None);
        await registry.ExecuteAsync(operation, customerB, CancellationToken.None);

        store.GetCustomerCallCount.Should().Be(2);
        store.GetOrdersByCustomerCallCount.Should().Be(2);

        await registry.ApplyAsync(
            ctx =>
                ctx.Apply(
                    () => store.UpdateCustomer(customerA, new CustomerPatch("Updated", null)),
                    WriteEffect.For().Changes(CustomerResources.Customer.Wildcard())
                ),
            CancellationToken.None
        );

        var resultA = await registry.ExecuteAsync(operation, customerA, CancellationToken.None);

        var resultB = await registry.ExecuteAsync(operation, customerB, CancellationToken.None);

        resultA.Score.Should().Be(20);
        resultB.Score.Should().Be(30);
        store.GetCustomerCallCount.Should().Be(4);
        store.GetOrdersByCustomerCallCount.Should().Be(4);
    }

    [Fact]
    public async Task ApplyAsync_Returns_Write_Result()
    {
        var store = new FakeStore();
        var customers = new TrackedCustomers(store);
        var orders = new TrackedOrders(store);
        var operation = new CustomerScoreOperation(customers, orders);
        var cacheStore = new InMemoryCacheStore();
        var registry = new OperationRegistry(cacheStore).Register(operation);

        var customerA = new CustomerId("A");

        var order = await registry.ApplyAsync(
            ctx => orders.Create(customerA, new CreateOrderInput(50m), ctx),
            CancellationToken.None
        );

        order.CustomerId.Should().Be(customerA);
        order.Total.Should().Be(50m);
        order.Id.Value.Should().StartWith("o");
    }

    [Fact]
    public async Task No_Stale_Edge_Accumulation_After_Invalidation_Recompute_Cycle()
    {
        var store = new FakeStore();
        var customers = new TrackedCustomers(store);
        var orders = new TrackedOrders(store);
        var operation = new CustomerScoreOperation(customers, orders);
        var cacheStore = new InMemoryCacheStore();
        var registry = new OperationRegistry(cacheStore).Register(operation);

        var customerA = new CustomerId("A");

        await registry.ExecuteAsync(operation, customerA, CancellationToken.None);
        store.GetCustomerCallCount.Should().Be(1);
        store.GetOrdersByCustomerCallCount.Should().Be(1);

        await registry.ApplyAsync(
            ctx => orders.Create(customerA, new CreateOrderInput(10m), ctx),
            CancellationToken.None
        );

        await registry.ExecuteAsync(operation, customerA, CancellationToken.None);
        store.GetCustomerCallCount.Should().Be(2);
        store.GetOrdersByCustomerCallCount.Should().Be(2);

        await registry.ApplyAsync(
            ctx => orders.Create(customerA, new CreateOrderInput(20m), ctx),
            CancellationToken.None
        );

        var result = await registry.ExecuteAsync(operation, customerA, CancellationToken.None);

        result.Score.Should().Be(50);
        store.GetCustomerCallCount.Should().Be(3);
        store.GetOrdersByCustomerCallCount.Should().Be(3);
    }

    [Fact]
    public async Task FlushAllAsync_Clears_All_Entries()
    {
        var store = new FakeStore();
        var customers = new TrackedCustomers(store);
        var orders = new TrackedOrders(store);
        var operation = new CustomerScoreOperation(customers, orders);
        var cacheStore = new InMemoryCacheStore();
        var registry = new OperationRegistry(cacheStore).Register(operation);

        var customerA = new CustomerId("A");
        var customerB = new CustomerId("B");

        await registry.ExecuteAsync(operation, customerA, CancellationToken.None);
        await registry.ExecuteAsync(operation, customerB, CancellationToken.None);

        store.GetCustomerCallCount.Should().Be(2);
        store.GetOrdersByCustomerCallCount.Should().Be(2);

        await registry.FlushAllAsync(CancellationToken.None);

        var resultA = await registry.ExecuteAsync(operation, customerA, CancellationToken.None);

        var resultB = await registry.ExecuteAsync(operation, customerB, CancellationToken.None);

        resultA.Score.Should().Be(20);
        resultB.Score.Should().Be(30);
        store.GetCustomerCallCount.Should().Be(4);
        store.GetOrdersByCustomerCallCount.Should().Be(4);
    }

    [Fact]
    public async Task CacheMiss_After_RemoveAsync_Replaces_Stale_Forward_Edges()
    {
        var store = new FakeStore();
        var customers = new TrackedCustomers(store);
        var orders = new TrackedOrders(store);
        var operation = new CustomerScoreOperation(customers, orders);
        var cacheStore = new InMemoryCacheStore();
        var registry = new OperationRegistry(cacheStore).Register(operation);

        var customerA = new CustomerId("A");

        var entryKey = operation.BuildEntryKey(customerA);

        await registry.ExecuteAsync(operation, customerA, CancellationToken.None);

        store.GetCustomerCallCount.Should().Be(1);
        store.GetOrdersByCustomerCallCount.Should().Be(1);

        var initialScore = await cacheStore.GetAsync<CustomerScore>(
            entryKey,
            CancellationToken.None
        );
        initialScore.Should().NotBeNull();
        initialScore!.Value.Score.Should().Be(20);

        await cacheStore.RemoveAsync(entryKey, CancellationToken.None);

        var cachedAfterRemove = await cacheStore.GetAsync<CustomerScore>(
            entryKey,
            CancellationToken.None
        );
        cachedAfterRemove.Should().BeNull();

        var result = await registry.ExecuteAsync(operation, customerA, CancellationToken.None);

        result.Score.Should().Be(20);
        store.GetCustomerCallCount.Should().Be(2);
        store.GetOrdersByCustomerCallCount.Should().Be(2);
    }
}
