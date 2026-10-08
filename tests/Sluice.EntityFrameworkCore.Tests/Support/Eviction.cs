namespace Sluice.EntityFrameworkCore.Tests.Support;

public static class Eviction
{
    private static readonly Query<string, string> _probe = new("ef-eviction-probe");

    /// <summary>
    /// Caches one value whose compute runs <paramref name="read"/>, invalidates <paramref name="invalidation"/>,
    /// and reports whether the value was evicted. Each probe uses a fresh cache, so no earlier invalidation can
    /// make the answer true by accident.
    /// </summary>
    public static async Task<bool> IsEvictedBy(
        Func<SluiceReads, CancellationToken, Task> read,
        IEnumerable<Dependency> invalidation
    )
    {
        await using var node = new SluiceNode();
        var runs = 0;

        async Task<string> Compute(SluiceReads reads, CancellationToken ct)
        {
            runs++;
            await read(reads, ct);
            return "value";
        }

        await node.Cache.GetOrSetAsync(_probe, "key", Compute);
        await node.Cache.InvalidateAsync(invalidation);
        await node.Cache.GetOrSetAsync(_probe, "key", Compute);
        return runs == 2;
    }

    public static Task<bool> IsEvictedBy(
        Func<SluiceReads, CancellationToken, Task> read,
        Dependency invalidation
    ) => IsEvictedBy(read, [invalidation]);
}
