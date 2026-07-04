namespace Sluice;

public sealed class InMemoryCacheStore : ICacheStore
{
    private readonly Dictionary<string, object> _store = [];

    public Task<CacheEntry<TValue>?> GetAsync<TValue>(string key, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var obj) && obj is CacheEntry<TValue> entry)
        {
            return Task.FromResult<CacheEntry<TValue>?>(entry);
        }
        return Task.FromResult<CacheEntry<TValue>?>(null);
    }

    public Task SetAsync<TValue>(string key, CacheEntry<TValue> entry, CancellationToken ct)
    {
        _store[key] = entry;
        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string key, CancellationToken ct) =>
        Task.FromResult(_store.Remove(key));

    public Task ClearAsync(CancellationToken ct)
    {
        _store.Clear();
        return Task.CompletedTask;
    }
}
