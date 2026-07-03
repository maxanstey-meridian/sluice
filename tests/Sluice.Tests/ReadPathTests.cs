namespace Sluice.Tests;

public sealed class ReadPathTests
{
    public sealed class CacheMissThenHit
    {
        [Fact]
        public async Task First_Call_Computes_Second_Returns_Cached()
        {
            var store = new FakeStore();
            var customers = new TrackedCustomers(store);
            var orders = new TrackedOrders(store);
            var operation = new CustomerScoreOperation(customers, orders);
            var cacheStore = new InMemoryCacheStore();
            var registry = new OperationRegistry(cacheStore).Register(operation);

            var customerId = new CustomerId("c1");

            var result1 = await registry.ExecuteAsync(
                operation,
                customerId,
                CancellationToken.None
            );
            result1.Score.Should().Be(90);

            var result2 = await registry.ExecuteAsync(
                operation,
                customerId,
                CancellationToken.None
            );
            result2.Score.Should().Be(90);

            store.GetOrdersByCustomerCallCount.Should().Be(1);
        }

        [Fact]
        public async Task Second_Call_Does_Not_Retrieve_Customer()
        {
            var store = new FakeStore();
            var customers = new TrackedCustomers(store);
            var orders = new TrackedOrders(store);
            var operation = new CustomerScoreOperation(customers, orders);
            var cacheStore = new InMemoryCacheStore();
            var registry = new OperationRegistry(cacheStore).Register(operation);

            var customerId = new CustomerId("c1");

            await registry.ExecuteAsync(operation, customerId, CancellationToken.None);

            store.GetCustomerCallCount.Should().Be(1);
        }
    }

    public sealed class ObservedReadsCaptured
    {
        [Fact]
        public async Task Cache_Entry_Stores_Read_Addresses()
        {
            var store = new FakeStore();
            var customers = new TrackedCustomers(store);
            var orders = new TrackedOrders(store);
            var operation = new CustomerScoreOperation(customers, orders);
            var cacheStore = new InMemoryCacheStore();
            var registry = new OperationRegistry(cacheStore).Register(operation);

            var customerId = new CustomerId("c1");
            await registry.ExecuteAsync(operation, customerId, CancellationToken.None);

            var entry = await cacheStore.GetAsync<CustomerScore>(
                "customer.score:v1:{\"customerId\":\"c1\"}",
                CancellationToken.None
            );

            entry.Should().NotBeNull();
            entry!.ObservedReads.Should().HaveCount(2);

            var entityAddr = new ResourceAddress(ResourceKind.Entity, "customer", "c1");
            var collectionAddr = new ResourceAddress(
                ResourceKind.Collection,
                "orders.byCustomer",
                "c1"
            );

            entry.ObservedReads.Should().Contain(entityAddr);
            entry.ObservedReads.Should().Contain(collectionAddr);
        }
    }

    public sealed class PathSensitivity
    {
        [Fact]
        public async Task Different_Keys_Produce_Different_Observed_Reads()
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

            var entryA = await cacheStore.GetAsync<CustomerScore>(
                "customer.score:v1:{\"customerId\":\"A\"}",
                CancellationToken.None
            );
            var entryB = await cacheStore.GetAsync<CustomerScore>(
                "customer.score:v1:{\"customerId\":\"B\"}",
                CancellationToken.None
            );

            entryA.Should().NotBeNull();
            entryB.Should().NotBeNull();

            var expectedAAddr = new ResourceAddress(ResourceKind.Entity, "customer", "A");
            var expectedBAddr = new ResourceAddress(ResourceKind.Entity, "customer", "B");

            entryA!.ObservedReads.Should().Contain(expectedAAddr);
            entryA.ObservedReads.Should().NotContain(expectedBAddr);

            entryB!.ObservedReads.Should().Contain(expectedBAddr);
            entryB.ObservedReads.Should().NotContain(expectedAAddr);
        }

        [Fact]
        public async Task Different_Keys_Produce_Different_Cache_Entries()
        {
            var store = new FakeStore();
            var customers = new TrackedCustomers(store);
            var orders = new TrackedOrders(store);
            var operation = new CustomerScoreOperation(customers, orders);
            var cacheStore = new InMemoryCacheStore();
            var registry = new OperationRegistry(cacheStore).Register(operation);

            var customerA = new CustomerId("A");
            var customerB = new CustomerId("B");

            var resultA = await registry.ExecuteAsync(operation, customerA, CancellationToken.None);
            var resultB = await registry.ExecuteAsync(operation, customerB, CancellationToken.None);

            resultA.Score.Should().Be(20);
            resultB.Score.Should().Be(30);
        }
    }

    public sealed class TrackedResourceRead
    {
        [Fact]
        public async Task Read_Helper_Records_Address()
        {
            var store = new FakeStore();
            var customers = new TrackedCustomers(store);
            var ctx = new OperationContext();

            var customerId = new CustomerId("c1");
            await customers.Get(customerId, ctx);

            var expected = new ResourceAddress(ResourceKind.Entity, "customer", "c1");
            ctx.ObservedReads.Should().ContainSingle();
            ctx.ObservedReads[0].Should().Be(expected);
        }
    }

    public sealed class MultipleEntries
    {
        [Fact]
        public async Task Separate_Entries_For_Different_Operations()
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

            var entryA = await cacheStore.GetAsync<CustomerScore>(
                "customer.score:v1:{\"customerId\":\"A\"}",
                CancellationToken.None
            );
            var entryB = await cacheStore.GetAsync<CustomerScore>(
                "customer.score:v1:{\"customerId\":\"B\"}",
                CancellationToken.None
            );

            entryA.Should().NotBeNull();
            entryB.Should().NotBeNull();
            entryA!.Value.Should().NotBeSameAs(entryB!.Value);
            entryA.ObservedReads.Should().NotBeEquivalentTo(entryB.ObservedReads);
        }
    }

    public sealed class VersionNamespacing
    {
        [Fact]
        public async Task Different_Versions_Produce_Different_Entry_Keys()
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

            var customerId = new CustomerId("c1");

            var resultV1 = await registry.ExecuteAsync(
                operationV1,
                customerId,
                CancellationToken.None
            );
            var resultV2 = await registry.ExecuteAsync(
                operationV2,
                customerId,
                CancellationToken.None
            );

            resultV1.Score.Should().Be(90);
            resultV2.Score.Should().Be(180);

            var entryV1 = await cacheStore.GetAsync<CustomerScore>(
                "customer.score:v1:{\"customerId\":\"c1\"}",
                CancellationToken.None
            );
            var entryV2 = await cacheStore.GetAsync<CustomerScore>(
                "customer.score:v2:{\"customerId\":\"c1\"}",
                CancellationToken.None
            );

            entryV1.Should().NotBeNull();
            entryV2.Should().NotBeNull();
        }
    }
}
