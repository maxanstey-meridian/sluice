namespace Sluice.Tests;

internal sealed class FakeStore : IStore
{
    public int GetCustomerCallCount { get; private set; }
    public int GetOrdersByCustomerCallCount { get; private set; }
    public int UpdateCustomerCallCount { get; private set; }
    public int CreateOrderCallCount { get; private set; }
    public int GetOrderCallCount { get; private set; }
    public int DeleteOrderCallCount { get; private set; }
    public int ReassignOrderCallCount { get; private set; }

    private readonly Lock _lock = new();
    private readonly Dictionary<CustomerId, Customer> _customers = new();
    private readonly Dictionary<CustomerId, List<Order>> _orders = new();
    private int _nextOrderId = 100;

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
        lock (_lock)
        {
            GetCustomerCallCount++;
            if (_customers.TryGetValue(id, out var customer))
            {
                return Task.FromResult(customer);
            }
            var newCustomer = new Customer(
                id,
                $"Customer {id.Value}",
                $"email-{id.Value}@test.com"
            );
            _customers[id] = newCustomer;
            return Task.FromResult(newCustomer);
        }
    }

    public Task<IReadOnlyList<Order>> GetOrdersByCustomer(CustomerId id)
    {
        lock (_lock)
        {
            GetOrdersByCustomerCallCount++;
            if (_orders.TryGetValue(id, out var orders))
            {
                return Task.FromResult<IReadOnlyList<Order>>(orders);
            }
            var newOrders = new List<Order> { new(new OrderId($"{id.Value}-order"), id, 100m) };
            _orders[id] = newOrders;
            return Task.FromResult<IReadOnlyList<Order>>(newOrders);
        }
    }

    public Task UpdateCustomer(CustomerId id, CustomerPatch patch)
    {
        lock (_lock)
        {
            UpdateCustomerCallCount++;
            var customer = _customers[id];
            _customers[id] = new Customer(
                id,
                patch.Name ?? customer.Name,
                patch.Email ?? customer.Email
            );
            return Task.CompletedTask;
        }
    }

    public Task<Order> CreateOrder(CustomerId customerId, CreateOrderInput input)
    {
        lock (_lock)
        {
            CreateOrderCallCount++;
            var orderId = new OrderId($"o{Interlocked.Increment(ref _nextOrderId)}");
            var order = new Order(orderId, customerId, input.Total);
            _orders[customerId].Add(order);
            return Task.FromResult(order);
        }
    }

    public Task<Order> GetOrder(OrderId orderId)
    {
        lock (_lock)
        {
            GetOrderCallCount++;
            foreach (var customerOrders in _orders.Values)
            {
                foreach (var order in customerOrders.Where(order => order.Id == orderId))
                {
                    return Task.FromResult(order);
                }
            }
            throw new KeyNotFoundException($"Order {orderId} not found");
        }
    }

    public Task DeleteOrder(OrderId orderId)
    {
        lock (_lock)
        {
            DeleteOrderCallCount++;
            foreach (var customerOrders in _orders.Values)
            {
                var order = customerOrders.FirstOrDefault(o => o.Id == orderId);
                if (order == null)
                {
                    continue;
                }

                customerOrders.Remove(order);
                return Task.CompletedTask;
            }
            throw new KeyNotFoundException($"Order {orderId} not found");
        }
    }

    public Task ReassignOrder(OrderId orderId, CustomerId newCustomerId)
    {
        lock (_lock)
        {
            ReassignOrderCallCount++;
            Order? order = null;
            CustomerId? foundOldCustomerId = null;
            foreach (var kvp in _orders)
            {
                var found = kvp.Value.FirstOrDefault(o => o.Id == orderId);
                if (found == null)
                {
                    continue;
                }

                order = found;
                foundOldCustomerId = kvp.Key;
                break;
            }

            if (order == null)
            {
                throw new KeyNotFoundException($"Order {orderId} not found");
            }

            if (foundOldCustomerId is not null)
            {
                _orders[foundOldCustomerId].Remove(order);
            }

            var updatedOrder = order with { CustomerId = newCustomerId };
            _orders[newCustomerId].Add(updatedOrder);
            return Task.CompletedTask;
        }
    }
}
