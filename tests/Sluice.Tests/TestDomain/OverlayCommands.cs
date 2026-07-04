namespace Sluice.Tests;

internal sealed class OverlayCommands(ISluice sluice, IStore store)
{
    public Task UpdateCustomer(CustomerId id, CustomerPatch patch, CancellationToken ct) =>
        sluice.Apply(
            _ => store.UpdateCustomer(id, patch),
            changes => changes.Changed(CustomerResources.Customer.For(id)),
            ct
        );

    public Task<Order> CreateOrder(
        CustomerId customerId,
        CreateOrderInput input,
        CancellationToken ct
    ) =>
        sluice.Apply(
            _ => store.CreateOrder(customerId, input),
            changes =>
                changes
                    .Changed(OrderResources.OrdersByCustomer.For(customerId))
                    .Changed(result => OrderResources.Order.For(result.Id)),
            ct
        );

    public async Task DeleteOrder(OrderId orderId, CancellationToken ct)
    {
        var existing = await store.GetOrder(orderId);
        await sluice.Apply(
            _ => store.DeleteOrder(orderId),
            changes =>
                changes
                    .Changed(OrderResources.Order.For(orderId))
                    .Changed(OrderResources.OrdersByCustomer.For(existing.CustomerId)),
            ct
        );
    }

    public async Task ReassignOrder(OrderId orderId, CustomerId newCustomerId, CancellationToken ct)
    {
        var existing = await store.GetOrder(orderId);
        await sluice.Apply(
            _ => store.ReassignOrder(orderId, newCustomerId),
            changes =>
                changes
                    .Changed(OrderResources.Order.For(orderId))
                    .Changed(OrderResources.OrdersByCustomer.For(existing.CustomerId))
                    .Changed(OrderResources.OrdersByCustomer.For(newCustomerId)),
            ct
        );
    }
}
