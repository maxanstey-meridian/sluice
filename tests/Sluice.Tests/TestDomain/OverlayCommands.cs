namespace Sluice.Tests;

internal sealed class OverlayCommands(ISluice sluice, IStore store)
{
    public Task UpdateCustomer(CustomerId id, CustomerPatch patch, CancellationToken ct) =>
        sluice.Apply(
            _ => store.UpdateCustomer(id, patch),
            CustomerWriteEffects.Updated(id),
            ct
        );

    public Task<Order> CreateOrder(
        CustomerId customerId,
        CreateOrderInput input,
        CancellationToken ct
    ) =>
        sluice.Apply(
            _ => store.CreateOrder(customerId, input),
            OrderWriteEffects.Created(customerId),
            ct
        );

    public async Task DeleteOrder(OrderId orderId, CancellationToken ct)
    {
        var existing = await store.GetOrder(orderId);
        await sluice.Apply(
            _ => store.DeleteOrder(orderId),
            OrderWriteEffects.Deleted(orderId, existing.CustomerId),
            ct
        );
    }

    public async Task ReassignOrder(OrderId orderId, CustomerId newCustomerId, CancellationToken ct)
    {
        var existing = await store.GetOrder(orderId);
        await sluice.Apply(
            _ => store.ReassignOrder(orderId, newCustomerId),
            OrderWriteEffects.Reassigned(orderId, existing.CustomerId, newCustomerId),
            ct
        );
    }
}
