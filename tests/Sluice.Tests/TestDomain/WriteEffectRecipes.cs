namespace Sluice.Tests;

internal static class CustomerWriteEffects
{
    public static WriteEffect Updated(CustomerId id) =>
        WriteEffect.For().Changes(CustomerResources.Customer.For(id));
}

internal static class OrderWriteEffects
{
    public static WriteEffect<Order> Created(CustomerId customerId) =>
        WriteEffect<Order>
            .For()
            .Changes(OrderResources.OrdersByCustomer.For(customerId))
            .ChangesResult(order => OrderResources.Order.For(order.Id));

    public static WriteEffect Deleted(OrderId orderId, CustomerId customerId) =>
        WriteEffect
            .For()
            .Changes(OrderResources.Order.For(orderId))
            .Changes(OrderResources.OrdersByCustomer.For(customerId));

    public static WriteEffect Reassigned(
        OrderId orderId,
        CustomerId oldCustomerId,
        CustomerId newCustomerId
    ) =>
        WriteEffect
            .For()
            .Changes(OrderResources.Order.For(orderId))
            .Changes(OrderResources.OrdersByCustomer.For(oldCustomerId))
            .Changes(OrderResources.OrdersByCustomer.For(newCustomerId));
}
