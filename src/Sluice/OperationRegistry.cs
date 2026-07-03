namespace Sluice;

public sealed class OperationRegistry(ICacheStore cacheStore)
{
    private readonly List<object> _registeredOperations = [];

    public OperationRegistry Register<TKey, TValue>(CachedOperation<TKey, TValue> operation)
    {
        _registeredOperations.Add(operation);
        return this;
    }

    public async Task<TValue> ExecuteAsync<TKey, TValue>(
        CachedOperation<TKey, TValue> operation,
        TKey key,
        CancellationToken ct
    )
    {
        var entryKey = operation.BuildEntryKey(key);

        var cached = await cacheStore.GetAsync<TValue>(entryKey, ct);
        if (cached is not null)
        {
            return cached.Value;
        }

        var ctx = new OperationContext(ct);
        var value = await operation.RunCompute(key, ctx);

        var entry = new CacheEntry<TValue>(value, [.. ctx.ObservedReads], DateTimeOffset.UtcNow);
        await cacheStore.SetAsync(entryKey, entry, ct);

        return value;
    }
}
