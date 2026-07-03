namespace Sluice.Tests;

internal static class CustomerResources
{
    public static readonly EntityResource<CustomerId> Customer = Resource.Entity<CustomerId>(
        "customer"
    );
}

internal static class OrderResources
{
    public static readonly EntityResource<OrderId> Order = Resource.Entity<OrderId>("order");

    public static readonly CollectionResource<CustomerId> OrdersByCustomer =
        Resource.Collection<CustomerId>("orders.byCustomer");
}
