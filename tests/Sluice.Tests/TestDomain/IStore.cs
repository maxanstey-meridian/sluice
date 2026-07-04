namespace Sluice.Tests;

internal interface IStore
{
    public Task<Customer> GetCustomer(CustomerId id);
    public Task<IReadOnlyList<Order>> GetOrdersByCustomer(CustomerId id);
    public Task UpdateCustomer(CustomerId id, CustomerPatch patch);
    public Task<Order> CreateOrder(CustomerId customerId, CreateOrderInput input);
    public Task<Order> GetOrder(OrderId orderId);
    public Task DeleteOrder(OrderId orderId);
    public Task ReassignOrder(OrderId orderId, CustomerId newCustomerId);
}
