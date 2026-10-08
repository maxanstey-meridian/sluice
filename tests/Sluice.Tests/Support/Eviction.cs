namespace Sluice.Tests.Support;

public static class Eviction
{
    private static readonly Query<string, string> _probe = new("eviction-probe");

    /// <summary>
    /// Caches one value whose compute runs <paramref name="record"/>, invalidates <paramref name="dependency"/>,
    /// and reports whether the value was evicted. Each probe uses a fresh cache, so no earlier invalidation can
    /// make the answer true by accident.
    /// </summary>
    public static async Task<bool> IsEvictedBy(
        Func<SluiceReads, Task> record,
        Dependency dependency
    )
    {
        await using var node = new SluiceNode();
        var runs = 0;

        async Task<string> Compute(SluiceReads read, CancellationToken ct)
        {
            runs++;
            await record(read);
            return "value";
        }

        await node.Cache.GetOrSetAsync(_probe, "key", Compute);
        await node.Cache.InvalidateAsync(dependency);
        await node.Cache.GetOrSetAsync(_probe, "key", Compute);
        return runs == 2;
    }

    public static Task<bool> IsEvictedBy(Action<SluiceReads> record, Dependency dependency) =>
        IsEvictedBy(
            read =>
            {
                record(read);
                return Task.CompletedTask;
            },
            dependency
        );
}
