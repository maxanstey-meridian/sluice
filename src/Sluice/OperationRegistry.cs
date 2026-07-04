using System.Text;

namespace Sluice;

public sealed class OperationRegistry(ICacheStore cacheStore)
{
    private readonly List<IOperation> _operationMetadata = [];
    private readonly Dictionary<ResourceAddress, HashSet<string>> _reverseIndex = [];
    private readonly Dictionary<string, HashSet<ResourceAddress>> _forwardIndex = [];
    private readonly Dictionary<string, DateTimeOffset> _cachedAt = [];

    public OperationRegistry Register<TKey, TValue>(CachedOperation<TKey, TValue> operation)
    {
        _operationMetadata.Add(operation);
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
            _cachedAt.Remove(entryKey);
            foreach (var address in oldEdges)
            {
                if (!_reverseIndex.TryGetValue(address, out var entryKeys))
                {
                    continue;
                }

                entryKeys.Remove(entryKey);
                if (entryKeys.Count == 0)
                {
                    _reverseIndex.Remove(address);
                }
            }
            _forwardIndex.Remove(entryKey);
        }

        var ctx = new OperationContext(ct);
        var value = await operation.RunCompute(key, ctx);

        var now = DateTimeOffset.UtcNow;
        var entry = new CacheEntry<TValue>(value, [.. ctx.ObservedReads], now);
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
        _cachedAt[entryKey] = now;

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
        _cachedAt.Clear();
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
                    if (storedAddress.Kind != address.Kind || storedAddress.Name != address.Name)
                    {
                        continue;
                    }

                    if (!_reverseIndex.TryGetValue(storedAddress, out var entryKeys))
                    {
                        continue;
                    }

                    foreach (var entryKey in entryKeys)
                    {
                        affectedEntryKeys.Add(entryKey);
                    }
                }
            }
            else
            {
                if (!_reverseIndex.TryGetValue(address, out var entryKeys))
                {
                    continue;
                }

                foreach (var entryKey in entryKeys)
                {
                    affectedEntryKeys.Add(entryKey);
                }
            }
        }

        foreach (var entryKey in affectedEntryKeys)
        {
            _cachedAt.Remove(entryKey);
            if (_forwardIndex.TryGetValue(entryKey, out var observedAddresses))
            {
                foreach (var address in observedAddresses)
                {
                    if (!_reverseIndex.TryGetValue(address, out var entryKeys))
                    {
                        continue;
                    }

                    entryKeys.Remove(entryKey);
                    if (entryKeys.Count == 0)
                    {
                        _reverseIndex.Remove(address);
                    }
                }
                _forwardIndex.Remove(entryKey);
            }
            await cacheStore.RemoveAsync(entryKey, ct);
        }
    }

    public string DumpGraph()
    {
        var sb = new StringBuilder();

        sb.AppendLine("OPERATIONS:");
        foreach (var (entryKey, reads) in _forwardIndex)
        {
            sb.AppendLine($"  {entryKey}");
            sb.AppendLine("    reads:");
            foreach (var addr in reads)
            {
                sb.AppendLine($"      {addr}");
            }
            if (_cachedAt.TryGetValue(entryKey, out var ts))
            {
                sb.AppendLine($"    cached: {ts:O}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("RESOURCE ADDRESSES:");
        foreach (var (address, entryKeys) in _reverseIndex)
        {
            sb.AppendLine($"  {address}");
            sb.AppendLine("    invalidates:");
            foreach (var ek in entryKeys)
            {
                sb.AppendLine($"      {ek}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public SystemManifest Describe()
    {
        var operations = _operationMetadata
            .Select(op => new OperationInfo(
                op.Name,
                op.KeyType.Name,
                op.ValueType.Name,
                op.GetType().Name
            ))
            .ToList();
        return new SystemManifest(operations);
    }
}
