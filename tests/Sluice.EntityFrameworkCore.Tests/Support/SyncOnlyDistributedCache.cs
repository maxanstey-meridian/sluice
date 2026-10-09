using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Sluice.EntityFrameworkCore.Tests.Support;

/// <summary>An in-memory distributed cache whose async methods throw, so any async call through it shows up.</summary>
public sealed class SyncOnlyDistributedCache : IDistributedCache
{
    private readonly MemoryDistributedCache _inner = new(
        Options.Create(new MemoryDistributedCacheOptions())
    );

    public byte[]? Get(string key) => _inner.Get(key);

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
        _inner.Set(key, value, options);

    public void Refresh(string key) => _inner.Refresh(key);

    public void Remove(string key) => _inner.Remove(key);

    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Async();

    public Task SetAsync(
        string key,
        byte[] value,
        DistributedCacheEntryOptions options,
        CancellationToken token = default
    ) => Async();

    public Task RefreshAsync(string key, CancellationToken token = default) => Async();

    public Task RemoveAsync(string key, CancellationToken token = default) => Async();

    private static Task<byte[]?> Async() =>
        throw new InvalidOperationException("An async call reached the sync-only cache.");
}
