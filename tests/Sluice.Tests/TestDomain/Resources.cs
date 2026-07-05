namespace Sluice.Tests;

internal static class CustomerResources
{
    public static readonly EntityResource<CustomerId> Customer = new("customer");
}

internal static class OrderResources
{
    public static readonly EntityResource<OrderId> Order = new("order");

    public static readonly CollectionResource<CustomerId> OrdersByCustomer = new(
        "orders.byCustomer"
    );
}
