namespace Sluice.Tests;

internal interface ITrackedCustomers
{
    public Task<Customer> Get(CustomerId id, OperationContext ctx);
}

internal interface ITrackedOrders
{
    public Task<IReadOnlyList<Order>> ByCustomer(CustomerId id, OperationContext ctx);
}

internal sealed class TrackedCustomers(IStore store)
    : TrackedResource("customers"),
        ITrackedCustomers
{
    public Task<Customer> Get(CustomerId id, OperationContext ctx) =>
        Read(ctx, CustomerResources.Customer.For(id), () => store.GetCustomer(id));
}

internal sealed class TrackedOrders(IStore store) : TrackedResource("orders"), ITrackedOrders
{
    public Task<IReadOnlyList<Order>> ByCustomer(CustomerId id, OperationContext ctx) =>
        Read(ctx, OrderResources.OrdersByCustomer.For(id), () => store.GetOrdersByCustomer(id));
}
