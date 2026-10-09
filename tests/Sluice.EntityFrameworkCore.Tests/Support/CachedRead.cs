using Microsoft.EntityFrameworkCore;

namespace Sluice.EntityFrameworkCore.Tests.Support;

/// <summary>A value cached on a node by a compute that runs one read, so a test can ask whether a write evicted it.</summary>
public sealed class CachedRead
{
    private static readonly Query<Guid, int> _query = new("ef-cached-read");

    private readonly SluiceCache _cache;
    private readonly Func<SluiceReads, CancellationToken, Task> _read;
    private readonly Guid _key = Guid.NewGuid();
    private int _computes;

    private CachedRead(SluiceCache cache, Func<SluiceReads, CancellationToken, Task> read)
    {
        _cache = cache;
        _read = read;
    }

    public static async Task<CachedRead> Of(
        SluiceCache cache,
        Func<SluiceReads, CancellationToken, Task> read
    )
    {
        var cached = new CachedRead(cache, read);
        await cached.Get();
        return cached;
    }

    /// <summary>
    /// Caches a value whose compute runs <paramref name="read"/>, invalidates <paramref name="invalidation"/>, and
    /// reports whether the value was evicted. Each probe uses a fresh node, so no earlier invalidation can make the
    /// answer true by accident.
    /// </summary>
    public static async Task<bool> IsEvictedBy(
        Func<SluiceReads, CancellationToken, Task> read,
        params IEnumerable<Dependency> invalidation
    )
    {
        await using var node = new SluiceNode();
        var cached = await Of(node.Cache, read);
        await node.Cache.InvalidateAsync(invalidation);
        return await cached.WasEvicted();
    }

    /// <summary>A cached read of customer <paramref name="id"/> through <paramref name="reader"/>.</summary>
    public static Task<CachedRead> Customer(SluiceCache cache, ShopDb reader, int id) =>
        Of(cache, (read, ct) => read.Entity(reader.Customers, id).SingleOrDefaultAsync(ct));

    /// <summary>A cached read of the orders of customer <paramref name="customerId"/> through <paramref name="reader"/>.</summary>
    public static Task<CachedRead> OrdersOf(SluiceCache cache, ShopDb reader, int customerId) =>
        Of(
            cache,
            (read, ct) =>
                read.Children(reader.Orders, o => o.CustomerId, customerId).ToListAsync(ct)
        );

    /// <summary>Reads the value again: true if it had been evicted and was recomputed (and so is cached again).</summary>
    public async Task<bool> WasEvicted()
    {
        var before = _computes;
        await Get();
        return _computes > before;
    }

    private async Task Get() =>
        await _cache.GetOrSetAsync(
            _query,
            _key,
            async (read, ct) =>
            {
                await _read(read, ct);
                return ++_computes;
            }
        );
}
