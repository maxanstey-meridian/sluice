namespace Sluice;

public interface ICacheStore
{
    public Task<CacheEntry<TValue>?> GetAsync<TValue>(string key, CancellationToken ct);
    public Task SetAsync<TValue>(string key, CacheEntry<TValue> entry, CancellationToken ct);
    public Task<bool> RemoveAsync(string key, CancellationToken ct);
    public Task ClearAsync(CancellationToken ct);
}
