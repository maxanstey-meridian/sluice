namespace Sluice.Tests;

internal sealed class FakeStore : IStore
{
    public int GetCustomerCallCount { get; private set; }
    public int GetOrdersByCustomerCallCount { get; private set; }

    private readonly Dictionary<CustomerId, Customer> _customers = new();
    private readonly Dictionary<CustomerId, List<Order>> _orders = new();

    public FakeStore()
    {
        var c1 = new CustomerId("c1");
        var c2 = new CustomerId("c2");
        var a = new CustomerId("A");
        var b = new CustomerId("B");

        _customers[c1] = new Customer(c1, "Alice", "alice@example.com");
        _customers[c2] = new Customer(c2, "Bob", "bob@example.com");
        _customers[a] = new Customer(a, "Alice", "alice@example.com");
        _customers[b] = new Customer(b, "Bob", "bob@example.com");

        _orders[c1] =
        [
            new Order(new OrderId("o1"), c1, 50m),
            new Order(new OrderId("o2"), c1, 40m),
        ];
        _orders[c2] = [new Order(new OrderId("o3"), c2, 30m)];
        _orders[a] = [new Order(new OrderId("o4"), a, 20m)];
        _orders[b] = [new Order(new OrderId("o5"), b, 30m)];
    }

    public Task<Customer> GetCustomer(CustomerId id)
    {
        GetCustomerCallCount++;
        return Task.FromResult(_customers[id]);
    }

    public Task<IReadOnlyList<Order>> GetOrdersByCustomer(CustomerId id)
    {
        GetOrdersByCustomerCallCount++;
        return Task.FromResult<IReadOnlyList<Order>>(_orders[id]);
    }
}
