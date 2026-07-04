namespace Sluice.Tests;

public sealed class DescribeTests
{
    [Fact]
    public void Registered_Operations_Returns_Correct_Metadata()
    {
        var store = new FakeStore();
        var customers = new TrackedCustomers(store);
        var orders = new TrackedOrders(store);
        var operation = new CustomerScoreOperation(customers, orders);
        var cacheStore = new InMemoryCacheStore();
        var registry = new OperationRegistry(cacheStore).Register(operation);

        var manifest = registry.Describe();

        manifest.Operations.Should().HaveCount(1);
        manifest.Operations[0].Name.Should().Be("customer.score");
        manifest.Operations[0].InputType.Should().Be("CustomerId");
        manifest.Operations[0].OutputType.Should().Be("CustomerScore");
        manifest.Operations[0].DefinedBy.Should().Be("CustomerScoreOperation");
    }

    [Fact]
    public void Multiple_Operations_Returns_All()
    {
        var store = new FakeStore();
        var customers = new TrackedCustomers(store);
        var orders = new TrackedOrders(store);
        var operationV1 = new CustomerScoreOperationV1(customers, orders);
        var operationV2 = new CustomerScoreOperationV2(customers, orders);
        var cacheStore = new InMemoryCacheStore();
        var registry = new OperationRegistry(cacheStore)
            .Register(operationV1)
            .Register(operationV2);

        var manifest = registry.Describe();

        manifest.Operations.Should().HaveCount(2);
        manifest.Operations[0].Name.Should().Be("customer.score");
        manifest.Operations[0].DefinedBy.Should().Be("CustomerScoreOperationV1");
        manifest.Operations[1].Name.Should().Be("customer.score");
        manifest.Operations[1].DefinedBy.Should().Be("CustomerScoreOperationV2");
    }

    [Fact]
    public void Static_No_Execution_Needed()
    {
        var cacheStore = new InMemoryCacheStore();
        var registry = new OperationRegistry(cacheStore);
        registry.Register(new CustomerScoreOperation(null!, null!));

        var manifest = registry.Describe();

        manifest.Operations.Should().HaveCount(1);
        manifest.Operations[0].Name.Should().Be("customer.score");
    }
}
