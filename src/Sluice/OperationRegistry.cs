namespace Sluice;

public sealed class OperationRegistry(ICacheStore cacheStore)
{
    private readonly List<object> _registeredOperations = [];
    private readonly Dictionary<ResourceAddress, HashSet<string>> _reverseIndex = [];
    private readonly Dictionary<string, HashSet<ResourceAddress>> _forwardIndex = [];

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

        if (_forwardIndex.TryGetValue(entryKey, out var oldEdges))
        {
            foreach (var address in oldEdges)
            {
                if (_reverseIndex.TryGetValue(address, out var entryKeys))
                {
                    entryKeys.Remove(entryKey);
                    if (entryKeys.Count == 0)
                    {
                        _reverseIndex.Remove(address);
                    }
                }
            }
            _forwardIndex.Remove(entryKey);
        }

        var ctx = new OperationContext(ct);
        var value = await operation.RunCompute(key, ctx);

        var entry = new CacheEntry<TValue>(value, [.. ctx.ObservedReads], DateTimeOffset.UtcNow);
        await cacheStore.SetAsync(entryKey, entry, ct);

        foreach (var address in ctx.ObservedReads)
        {
            if (!_reverseIndex.TryGetValue(address, out var entryKeys))
            {
                entryKeys = [];
                _reverseIndex[address] = entryKeys;
            }
            entryKeys.Add(entryKey);
        }
        _forwardIndex[entryKey] = [.. ctx.ObservedReads];

        return value;
    }

    public async Task ApplyAsync(Func<ChangeContext, Task> write, CancellationToken ct)
    {
        var ctx = new ChangeContext(ct);
        await write(ctx);
        await InvalidateAsync(ctx.ChangedAddresses, ct);
    }

    public async Task<T> ApplyAsync<T>(Func<ChangeContext, Task<T>> write, CancellationToken ct)
    {
        var ctx = new ChangeContext(ct);
        var result = await write(ctx);
        await InvalidateAsync(ctx.ChangedAddresses, ct);
        return result;
    }

    public async Task FlushAllAsync(CancellationToken ct)
    {
        await cacheStore.ClearAsync(ct);
        _reverseIndex.Clear();
        _forwardIndex.Clear();
    }

    private async Task InvalidateAsync(
        IReadOnlyList<ResourceAddress> changedAddresses,
        CancellationToken ct
    )
    {
        var affectedEntryKeys = new HashSet<string>();

        foreach (var address in changedAddresses)
        {
            if (address.Key == "*")
            {
                foreach (var storedAddress in _reverseIndex.Keys)
                {
                    if (storedAddress.Kind == address.Kind && storedAddress.Name == address.Name)
                    {
                        if (_reverseIndex.TryGetValue(storedAddress, out var entryKeys))
                        {
                            foreach (var entryKey in entryKeys)
                            {
                                affectedEntryKeys.Add(entryKey);
                            }
                        }
                    }
                }
            }
            else
            {
                if (_reverseIndex.TryGetValue(address, out var entryKeys))
                {
                    foreach (var entryKey in entryKeys)
                    {
                        affectedEntryKeys.Add(entryKey);
                    }
                }
            }
        }

        foreach (var entryKey in affectedEntryKeys)
        {
            if (_forwardIndex.TryGetValue(entryKey, out var observedAddresses))
            {
                foreach (var address in observedAddresses)
                {
                    if (_reverseIndex.TryGetValue(address, out var entryKeys))
                    {
                        entryKeys.Remove(entryKey);
                        if (entryKeys.Count == 0)
                        {
                            _reverseIndex.Remove(address);
                        }
                    }
                }
                _forwardIndex.Remove(entryKey);
            }
            await cacheStore.RemoveAsync(entryKey, ct);
        }
    }
}
