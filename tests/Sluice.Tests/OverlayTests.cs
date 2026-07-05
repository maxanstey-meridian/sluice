namespace Sluice.Tests;

public sealed class OverlayTests
{
    private static (
        FakeStore store,
        OverlayQueries queries,
        SluiceKernel sluice,
        OrderSluice commands
    ) CreateSut()
    {
        var store = new FakeStore();
        var queries = new OverlayQueries(store);
        var sluice = new SluiceKernel(new InMemoryCacheStore());
        var commands = new OrderSluice(sluice, store);
        return (store, queries, sluice, commands);
    }

    [Fact]
    public async Task Get_IsCacheFirst()
    {
        var (store, queries, sluice, _) = CreateSut();
        var id = new CustomerId("c1");

        var result1 = await sluice.Get(queries.CustomerScore, id, CancellationToken.None);
        result1.Score.Should().Be(90);

        var result2 = await sluice.Get(queries.CustomerScore, id, CancellationToken.None);
        result2.Score.Should().Be(90);

        store.GetCustomerCallCount.Should().Be(1);
        store.GetOrdersByCustomerCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Apply_StaticChanges_InvalidatesCache()
    {
        var (store, queries, sluice, commands) = CreateSut();
        var id = new CustomerId("c1");

        var score1 = await sluice.Get(queries.CustomerScore, id, CancellationToken.None);
        score1.Score.Should().Be(90);

        var initialCustomerCallCount = store.GetCustomerCallCount;
        var initialOrderCallCount = store.GetOrdersByCustomerCallCount;

        await commands.UpdateCustomer(
            id,
            new CustomerPatch("Alice Updated", null),
            CancellationToken.None
        );

        var score2 = await sluice.Get(queries.CustomerScore, id, CancellationToken.None);
        score2.Score.Should().Be(90);

        store.GetCustomerCallCount.Should().BeGreaterThan(initialCustomerCallCount);
        store.GetOrdersByCustomerCallCount.Should().BeGreaterThan(initialOrderCallCount);
    }

    [Fact]
    public async Task Apply_ResultDerived_InvalidatesCache()
    {
        var (store, queries, sluice, commands) = CreateSut();
        var customerId = new CustomerId("A");

        var score1 = await sluice.Get(queries.CustomerScore, customerId, CancellationToken.None);
        score1.Score.Should().Be(20);

        var order = await commands.CreateOrder(
            customerId,
            new CreateOrderInput(10m),
            CancellationToken.None
        );
        order.Id.Value.Should().StartWith("o");

        var score2 = await sluice.Get(queries.CustomerScore, customerId, CancellationToken.None);
        score2.Score.Should().Be(30);
    }

    [Fact]
    public async Task Apply_MultipleChangesInOneScope()
    {
        var (store, queries, sluice, commands) = CreateSut();
        var customerId = new CustomerId("A");

        var score1 = await sluice.Get(queries.CustomerScore, customerId, CancellationToken.None);
        score1.Score.Should().Be(20);

        var order = await store.GetOrder(new OrderId("o4"));
        await commands.UpdateCustomer(
            customerId,
            new CustomerPatch("Alice Two", null),
            CancellationToken.None
        );
        await commands.DeleteOrder(order.Id, CancellationToken.None);

        var score2 = await sluice.Get(queries.CustomerScore, customerId, CancellationToken.None);
        score2.Score.Should().Be(0);
    }

    [Fact]
    public async Task DeleteOrder_InvalidatesOldCollection()
    {
        var (store, queries, sluice, commands) = CreateSut();
        var customerId = new CustomerId("A");

        var score1 = await sluice.Get(queries.CustomerScore, customerId, CancellationToken.None);
        score1.Score.Should().Be(20);

        await commands.DeleteOrder(new OrderId("o4"), CancellationToken.None);

        var score2 = await sluice.Get(queries.CustomerScore, customerId, CancellationToken.None);
        score2.Score.Should().Be(0);
    }

    [Fact]
    public async Task ReassignOrder_InvalidatesBothCollections()
    {
        var (store, queries, sluice, commands) = CreateSut();
        var customerA = new CustomerId("A");
        var customerB = new CustomerId("B");

        var scoreA = await sluice.Get(queries.CustomerScore, customerA, CancellationToken.None);
        var scoreB = await sluice.Get(queries.CustomerScore, customerB, CancellationToken.None);
        scoreA.Score.Should().Be(20);
        scoreB.Score.Should().Be(30);

        await commands.ReassignOrder(new OrderId("o4"), customerB, CancellationToken.None);

        var scoreA2 = await sluice.Get(queries.CustomerScore, customerA, CancellationToken.None);
        var scoreB2 = await sluice.Get(queries.CustomerScore, customerB, CancellationToken.None);
        scoreA2.Score.Should().Be(0);
        scoreB2.Score.Should().Be(50);
    }

    [Fact]
    public async Task Track_RecordsObservedReads()
    {
        var (store, queries, sluice, _) = CreateSut();
        var id = new CustomerId("c1");

        await sluice.Get(queries.CustomerScore, id, CancellationToken.None);

        var graph = sluice.DumpGraph();

        graph.Should().Contain("entity:customer:c1");
        graph.Should().Contain("collection:orders.byCustomer:c1");
        graph.Should().Contain("customer.score:v1:{\"customerId\":\"c1\"}");
    }

    [Fact]
    public async Task Query_Define_ProducesManifest()
    {
        var (store, queries, sluice, _) = CreateSut();
        var id = new CustomerId("c1");

        await sluice.Get(queries.CustomerScore, id, CancellationToken.None);

        var manifest = sluice.Describe();

        manifest.Operations.Should().ContainSingle();
        var op = manifest.Operations[0];
        op.Name.Should().Be("customer.score");
        op.InputType.Should().Be("CustomerId");
        op.OutputType.Should().Be("CustomerScore");
        op.DefinedBy.Should().Be("DelegateCachedOperation`2");
    }

    [Fact]
    public async Task FlushAllAsync_ClearsOverlayCache()
    {
        var (store, queries, sluice, _) = CreateSut();
        var id = new CustomerId("c1");

        var result1 = await sluice.Get(queries.CustomerScore, id, CancellationToken.None);
        result1.Score.Should().Be(90);
        store.GetCustomerCallCount.Should().Be(1);

        await sluice.FlushAllAsync(CancellationToken.None);

        var result2 = await sluice.Get(queries.CustomerScore, id, CancellationToken.None);
        result2.Score.Should().Be(90);

        store.GetCustomerCallCount.Should().Be(2);
    }

    [Fact]
    public async Task Track_ConvenienceOverload_RecordsObservedRead()
    {
        var (store, queries, sluice, _) = CreateSut();
        var id = new CustomerId("c1");

        var customer = await sluice.Get(queries.CustomerScoreDirect, id, CancellationToken.None);
        customer.Id.Should().Be(id);

        var graph = sluice.DumpGraph();
        graph.Should().Contain("entity:customer:c1");
    }

    [Fact]
    public async Task Query_LazyRegistration_NotInDescribeBeforeFirstGet()
    {
        var (store, queries, sluice, _) = CreateSut();

        var preManifest = sluice.Describe();
        preManifest.Operations.Should().BeEmpty();

        var id = new CustomerId("c1");
        await sluice.Get(queries.CustomerScore, id, CancellationToken.None);

        var postManifest = sluice.Describe();
        postManifest.Operations.Should().ContainSingle();
        postManifest.Operations[0].Name.Should().Be("customer.score");
    }

    [Fact]
    public async Task Apply_WriteEffectOverload_StaticChanges_InvalidatesCache()
    {
        var (store, queries, sluice, _) = CreateSut();
        var id = new CustomerId("c1");

        var score1 = await sluice.Get(queries.CustomerScore, id, CancellationToken.None);
        score1.Score.Should().Be(90);

        await sluice.Apply(
            _ => store.UpdateCustomer(id, new CustomerPatch("Updated", null)),
            CustomerWriteEffects.Updated(id),
            CancellationToken.None
        );

        var score2 = await sluice.Get(queries.CustomerScore, id, CancellationToken.None);
        score2.Score.Should().Be(90);
        store.GetCustomerCallCount.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task Apply_WriteEffectOverload_ResultDerived_InvalidatesCache()
    {
        var (store, queries, sluice, _) = CreateSut();
        var customerId = new CustomerId("A");

        var score1 = await sluice.Get(queries.CustomerScore, customerId, CancellationToken.None);
        score1.Score.Should().Be(20);

        var order = await sluice.Apply(
            _ => store.CreateOrder(customerId, new CreateOrderInput(10m)),
            OrderWriteEffects.Created(customerId),
            CancellationToken.None
        );

        order.Id.Value.Should().StartWith("o");

        var score2 = await sluice.Get(queries.CustomerScore, customerId, CancellationToken.None);
        score2.Score.Should().Be(30);
    }
}
