namespace Sluice.Tests;

internal sealed class OverlayQueries(IStore store)
{
    public readonly Query<CustomerId, CustomerScore> CustomerScore = new(
        "customer.score",
        id => new { customerId = id.Value },
        async (id, read) =>
        {
            _ = await read.Track(
                CustomerResources.Customer.For(id),
                _ => store.GetCustomer(id)
            );
            var orders = await read.Track(
                OrderResources.OrdersByCustomer.For(id),
                _ => store.GetOrdersByCustomer(id)
            );
            return new CustomerScore(id, (int)orders.Sum(o => o.Total));
        }
    );

    public readonly Query<CustomerId, Customer> CustomerScoreDirect = new(
        "customer.score.direct",
        id => new { customerId = id.Value },
        async (id, read) =>
        {
            return await read.Track(
                CustomerResources.Customer.For(id),
                () => store.GetCustomer(id)
            );
        }
    );
}
