namespace Sluice.Tests;

internal static class CustomerWriteEffects
{
    public static WriteEffect Updated(CustomerId id) => new(CustomerResources.Customer.For(id));
}

internal static class OrderWriteEffects
{
    public static WriteEffect<Order> Created(CustomerId customerId) =>
        new WriteEffect<Order>(OrderResources.OrdersByCustomer.For(customerId)).ChangesResult(
            order => OrderResources.Order.For(order.Id)
        );

    public static WriteEffect Deleted(OrderId orderId, CustomerId customerId) =>
        new(
            OrderResources.Order.For(orderId),
            OrderResources.OrdersByCustomer.For(customerId)
        );

    public static WriteEffect Reassigned(
        OrderId orderId,
        CustomerId oldCustomerId,
        CustomerId newCustomerId
    ) =>
        new(
            OrderResources.Order.For(orderId),
            OrderResources.OrdersByCustomer.For(oldCustomerId),
            OrderResources.OrdersByCustomer.For(newCustomerId)
        );
}
