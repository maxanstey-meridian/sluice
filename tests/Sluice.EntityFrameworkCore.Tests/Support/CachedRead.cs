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
