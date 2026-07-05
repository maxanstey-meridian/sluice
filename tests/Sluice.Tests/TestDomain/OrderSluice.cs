namespace Sluice.Tests;

internal sealed class OrderSluice(ISluice sluice, IStore store)
{
    private readonly TrackedWrite<CustomerId> _updateCustomer = new(
        sluice,
        CustomerResources.Customer.For
    );

    private readonly TrackedWrite<CustomerId, Order> _createOrder = new(
        sluice,
        [OrderResources.OrdersByCustomer.For],
        o => OrderResources.Order.For(o.Id)
    );

    private readonly TrackedWrite<OrderId, DeletedOrder> _deleteOrder = new(
        sluice,
        [OrderResources.Order.For],
        d => OrderResources.OrdersByCustomer.For(d.CustomerId)
    );

    private readonly TrackedWrite<OrderId, ReassignedOrder> _reassignOrder = new(
        sluice,
        [OrderResources.Order.For],
        d => OrderResources.OrdersByCustomer.For(d.OldCustomerId),
        d => OrderResources.OrdersByCustomer.For(d.NewCustomerId)
    );

    public Task UpdateCustomer(CustomerId id, CustomerPatch patch, CancellationToken ct) =>
        _updateCustomer.Write(id, _ => store.UpdateCustomer(id, patch), ct);

    public Task<Order> CreateOrder(
        CustomerId customerId,
        CreateOrderInput input,
        CancellationToken ct
    ) => _createOrder.Write(customerId, _ => store.CreateOrder(customerId, input), ct);

    public Task DeleteOrder(OrderId orderId, CancellationToken ct) =>
        _deleteOrder.Write(
            orderId,
            async ct2 =>
            {
                var existing = await store.GetOrder(orderId);
                await store.DeleteOrder(orderId);
                return new DeletedOrder(orderId, existing.CustomerId);
            },
            ct
        );

    public Task ReassignOrder(OrderId orderId, CustomerId newCustomerId, CancellationToken ct) =>
        _reassignOrder.Write(
            orderId,
            async ct2 =>
            {
                var existing = await store.GetOrder(orderId);
                await store.ReassignOrder(orderId, newCustomerId);
                return new ReassignedOrder(orderId, existing.CustomerId, newCustomerId);
            },
            ct
        );
}
