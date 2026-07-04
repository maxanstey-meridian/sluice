namespace Sluice.Tests;

internal interface ITrackedCustomers
{
    public Task<Customer> Get(CustomerId id, OperationContext ctx);
    public Task Update(CustomerId id, CustomerPatch patch, ChangeContext ctx);
}

internal interface ITrackedOrders
{
    public Task<IReadOnlyList<Order>> ByCustomer(CustomerId id, OperationContext ctx);
    public Task<Order> Create(CustomerId customerId, CreateOrderInput input, ChangeContext ctx);
    public Task Delete(OrderId orderId, ChangeContext ctx);
    public Task Reassign(OrderId orderId, CustomerId newCustomerId, ChangeContext ctx);
}

internal sealed class TrackedCustomers(IStore store)
    : TrackedResource("customers"),
        ITrackedCustomers
{
    public Task<Customer> Get(CustomerId id, OperationContext ctx) =>
        Read(ctx, CustomerResources.Customer.For(id), () => store.GetCustomer(id));

    public Task Update(CustomerId id, CustomerPatch patch, ChangeContext ctx) =>
        ctx.Apply(() => store.UpdateCustomer(id, patch), CustomerWriteEffects.Updated(id));
}

internal sealed class TrackedOrders(IStore store) : TrackedResource("orders"), ITrackedOrders
{
    public Task<IReadOnlyList<Order>> ByCustomer(CustomerId id, OperationContext ctx) =>
        Read(ctx, OrderResources.OrdersByCustomer.For(id), () => store.GetOrdersByCustomer(id));

    public Task<Order> Create(CustomerId customerId, CreateOrderInput input, ChangeContext ctx) =>
        ctx.Apply(
            () => store.CreateOrder(customerId, input),
            OrderWriteEffects.Created(customerId)
        );

    public async Task Delete(OrderId orderId, ChangeContext ctx)
    {
        var existing = await store.GetOrder(orderId);
        await ctx.Apply(
            () => store.DeleteOrder(orderId),
            OrderWriteEffects.Deleted(orderId, existing.CustomerId)
        );
    }

    public async Task Reassign(OrderId orderId, CustomerId newCustomerId, ChangeContext ctx)
    {
        var existing = await store.GetOrder(orderId);
        await ctx.Apply(
            () => store.ReassignOrder(orderId, newCustomerId),
            OrderWriteEffects.Reassigned(orderId, existing.CustomerId, newCustomerId)
        );
    }
}
