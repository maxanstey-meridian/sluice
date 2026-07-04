namespace Sluice.Tests;

public sealed class DumpGraphTests
{
    [Fact]
    public void Empty_Graph_Shows_Empty_Sections()
    {
        var cacheStore = new InMemoryCacheStore();
        var registry = new OperationRegistry(cacheStore);

        var graph = registry.DumpGraph();

        graph.Should().Contain("OPERATIONS:");
        graph.Should().Contain("RESOURCE ADDRESSES:");
        graph.Should().NotContain("customer.score");
    }

    [Fact]
    public async Task Single_Entry_Shows_Operations_And_Reverse_Edges()
    {
        var store = new FakeStore();
        var customers = new TrackedCustomers(store);
        var orders = new TrackedOrders(store);
        var operation = new CustomerScoreOperation(customers, orders);
        var cacheStore = new InMemoryCacheStore();
        var registry = new OperationRegistry(cacheStore).Register(operation);

        var customerId = new CustomerId("c1");
        await registry.ExecuteAsync(operation, customerId, CancellationToken.None);

        var graph = registry.DumpGraph();

        graph.Should().Contain("customer.score:v1:{\"customerId\":\"c1\"}");
        graph.Should().Contain("entity:customer:c1");
        graph.Should().Contain("collection:orders.byCustomer:c1");
        graph.Should().Contain("cached:");
        graph.Should().Contain("invalidates:");
        graph.Should().Contain("customer.score:v1:{\"customerId\":\"c1\"}");
    }

    [Fact]
    public async Task Multiple_Entries_Shows_All_Entries()
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

        var graph = registry.DumpGraph();

        graph.Should().Contain("customer.score:v1:{\"customerId\":\"A\"}");
        graph.Should().Contain("customer.score:v1:{\"customerId\":\"B\"}");
        graph.Should().Contain("entity:customer:A");
        graph.Should().Contain("entity:customer:B");
    }

    [Fact]
    public async Task Graph_After_Invalidation_Shows_Entry_Gone()
    {
        var store = new FakeStore();
        var customers = new TrackedCustomers(store);
        var orders = new TrackedOrders(store);
        var operation = new CustomerScoreOperation(customers, orders);
        var cacheStore = new InMemoryCacheStore();
        var registry = new OperationRegistry(cacheStore).Register(operation);

        var customerId = new CustomerId("c1");
        await registry.ExecuteAsync(operation, customerId, CancellationToken.None);

        await registry.ApplyAsync(
            ctx => customers.Update(customerId, new CustomerPatch("Updated", null), ctx),
            CancellationToken.None
        );

        var graph = registry.DumpGraph();

        graph.Should().NotContain("customer.score:v1:{\"customerId\":\"c1\"}");
    }

    [Fact]
    public async Task Graph_After_Recompute_Shows_Fresh_Entry()
    {
        var store = new FakeStore();
        var customers = new TrackedCustomers(store);
        var orders = new TrackedOrders(store);
        var operation = new CustomerScoreOperation(customers, orders);
        var cacheStore = new InMemoryCacheStore();
        var registry = new OperationRegistry(cacheStore).Register(operation);

        var customerId = new CustomerId("c1");
        await registry.ExecuteAsync(operation, customerId, CancellationToken.None);

        var graph1 = registry.DumpGraph();
        graph1.Should().Contain("customer.score:v1:{\"customerId\":\"c1\"}");

        await registry.ApplyAsync(
            ctx => customers.Update(customerId, new CustomerPatch("Updated", null), ctx),
            CancellationToken.None
        );

        var graph2 = registry.DumpGraph();
        graph2.Should().NotContain("customer.score:v1:{\"customerId\":\"c1\"}");

        var result = await registry.ExecuteAsync(operation, customerId, CancellationToken.None);
        result.Score.Should().Be(90);

        var graph3 = registry.DumpGraph();
        graph3.Should().Contain("customer.score:v1:{\"customerId\":\"c1\"}");
        graph3.Should().Contain("entity:customer:c1");
        graph3.Should().Contain("collection:orders.byCustomer:c1");
    }
}
