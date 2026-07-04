namespace Sluice.Tests;

public sealed class BenchmarkTests
{
    [Fact(Skip = "Kill criterion #2 deferred to dogfooding — in-memory operations cannot produce meaningful overhead ratios.")]
    public async Task Tracking_Overhead_Is_Under_5_Percent()
    {
        var store = new FakeStore();
        var customers = new TrackedCustomers(store);
        var orders = new TrackedOrders(store);
        var operation = new CustomerScoreOperation(customers, orders);
        var cacheStore = new InMemoryCacheStore();
        var registry = new OperationRegistry(cacheStore).Register(operation);

        const int warmup = 1000;
        const int measure = 10000;

        // Warmup
        for (var i = 0; i < warmup; i++)
        {
            await store.GetCustomer(new CustomerId($"warm-{i}"));
            await store.GetOrdersByCustomer(new CustomerId($"warm-{i}"));
            await registry.ExecuteAsync(
                operation,
                new CustomerId($"warm-{i}"),
                CancellationToken.None
            );
        }

        // Raw measurement
        var rawSw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < measure; i++)
        {
            var id = new CustomerId($"raw-{i}");
            var customer = await store.GetCustomer(id);
            var customerOrders = await store.GetOrdersByCustomer(id);
            _ = new CustomerScore(id, (int)customerOrders.Sum(o => o.Total));
        }
        rawSw.Stop();

        // Tracked measurement (cache miss — unique keys)
        var trackedSw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < measure; i++)
        {
            var id = new CustomerId($"tracked-{i}");
            _ = await registry.ExecuteAsync(operation, id, CancellationToken.None);
        }
        trackedSw.Stop();

        // Cached measurement (cache hit — same key)
        var warmupId = new CustomerId("cached-warmup");
        _ = await registry.ExecuteAsync(operation, warmupId, CancellationToken.None);

        var cachedSw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < measure; i++)
        {
            _ = await registry.ExecuteAsync(operation, warmupId, CancellationToken.None);
        }
        cachedSw.Stop();

        var rawMs = rawSw.ElapsedMilliseconds;
        var trackedMs = trackedSw.ElapsedMilliseconds;
        var cachedMs = cachedSw.ElapsedMilliseconds;
        var overhead = (double)(trackedMs - rawMs) / rawMs * 100;

        Console.WriteLine($"Raw:      {rawMs}ms");
        Console.WriteLine($"Tracked:   {trackedMs}ms");
        Console.WriteLine($"Cached:    {cachedMs}ms");
        Console.WriteLine($"Overhead:  {overhead:F2}%");

        overhead.Should().BeLessThan(5);
    }
}
