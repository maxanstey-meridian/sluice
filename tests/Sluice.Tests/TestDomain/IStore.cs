namespace Sluice.Tests;

internal interface IStore
{
    public Task<Customer> GetCustomer(CustomerId id);
    public Task<IReadOnlyList<Order>> GetOrdersByCustomer(CustomerId id);
}
