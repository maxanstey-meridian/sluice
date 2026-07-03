namespace Sluice.Tests;

internal sealed class CustomerScoreOperation(ITrackedCustomers customers, ITrackedOrders orders)
    : CachedOperation<CustomerId, CustomerScore>("customer.score")
{
    protected override CacheKey Key(CustomerId id) => CacheKey.From(new { customerId = id.Value });

    protected override async ValueTask<CustomerScore> Compute(CustomerId id, OperationContext ctx)
    {
        var customer = await customers.Get(id, ctx);
        var customerOrders = await orders.ByCustomer(id, ctx);
        return new CustomerScore(id, (int)customerOrders.Sum(o => o.Total));
    }
}

internal sealed class CustomerScoreOperationV1(ITrackedCustomers customers, ITrackedOrders orders)
    : CachedOperation<CustomerId, CustomerScore>("customer.score", version: 1)
{
    protected override CacheKey Key(CustomerId id) => CacheKey.From(new { customerId = id.Value });

    protected override async ValueTask<CustomerScore> Compute(CustomerId id, OperationContext ctx)
    {
        var customer = await customers.Get(id, ctx);
        var customerOrders = await orders.ByCustomer(id, ctx);
        return new CustomerScore(id, (int)customerOrders.Sum(o => o.Total));
    }
}

internal sealed class CustomerScoreOperationV2(ITrackedCustomers customers, ITrackedOrders orders)
    : CachedOperation<CustomerId, CustomerScore>("customer.score", version: 2)
{
    protected override CacheKey Key(CustomerId id) => CacheKey.From(new { customerId = id.Value });

    protected override async ValueTask<CustomerScore> Compute(CustomerId id, OperationContext ctx)
    {
        var customer = await customers.Get(id, ctx);
        var customerOrders = await orders.ByCustomer(id, ctx);
        return new CustomerScore(id, (int)(customerOrders.Sum(o => o.Total) * 2));
    }
}
