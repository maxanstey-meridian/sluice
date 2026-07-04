using System.Collections.Concurrent;
using System.Text;

namespace Sluice;

public sealed class OperationRegistry(ICacheStore cacheStore) : IDisposable
{
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly ConcurrentBag<IOperation> _operationMetadata = [];
    private readonly ConcurrentDictionary<ResourceAddress, HashSet<string>> _reverseIndex = new();
    private readonly ConcurrentDictionary<string, HashSet<ResourceAddress>> _forwardIndex = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cachedAt = new();

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

        _lock.EnterWriteLock();
        try
        {
            if (_forwardIndex.TryGetValue(entryKey, out var oldEdges))
            {
                _cachedAt.TryRemove(entryKey, out _);
                foreach (var address in oldEdges)
                {
                    if (!_reverseIndex.TryGetValue(address, out var entryKeys))
                    {
                        continue;
                    }

                    entryKeys.Remove(entryKey);
                    if (entryKeys.Count == 0)
                    {
                        _reverseIndex.TryRemove(address, out _);
                    }
                }
                _forwardIndex.TryRemove(entryKey, out _);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        var ctx = new OperationContext(ct);
        var value = await operation.RunCompute(key, ctx);

        var now = DateTimeOffset.UtcNow;
        var entry = new CacheEntry<TValue>(value, [.. ctx.ObservedReads], now);

        await cacheStore.SetAsync(entryKey, entry, ct);

        _lock.EnterWriteLock();
        try
        {
            foreach (var address in ctx.ObservedReads)
            {
                _reverseIndex.GetOrAdd(address, _ => new HashSet<string>()).Add(entryKey);
            }
            _forwardIndex[entryKey] = [.. ctx.ObservedReads];
            _cachedAt[entryKey] = now;
        }
        finally
        {
            _lock.ExitWriteLock();
        }

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

        _lock.EnterWriteLock();
        try
        {
            _reverseIndex.Clear();
            _forwardIndex.Clear();
            _cachedAt.Clear();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private async Task InvalidateAsync(
        IReadOnlyList<ResourceAddress> changedAddresses,
        CancellationToken ct
    )
    {
        HashSet<string> affectedEntryKeys;

        _lock.EnterWriteLock();
        try
        {
            affectedEntryKeys = [];

            foreach (var address in changedAddresses)
            {
                if (address.Key == "*")
                {
                    foreach (var storedAddress in _reverseIndex.Keys.ToArray())
                    {
                        if (
                            storedAddress.Kind != address.Kind
                            || storedAddress.Name != address.Name
                        )
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
                _cachedAt.TryRemove(entryKey, out _);
                if (!_forwardIndex.TryGetValue(entryKey, out var observedAddresses))
                {
                    continue;
                }

                foreach (var address in observedAddresses)
                {
                    if (!_reverseIndex.TryGetValue(address, out var entryKeys))
                    {
                        continue;
                    }

                    entryKeys.Remove(entryKey);
                    if (entryKeys.Count == 0)
                    {
                        _reverseIndex.TryRemove(address, out _);
                    }
                }
                _forwardIndex.TryRemove(entryKey, out _);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        foreach (var entryKey in affectedEntryKeys)
        {
            await cacheStore.RemoveAsync(entryKey, ct);
        }
    }

    public string DumpGraph()
    {
        _lock.EnterReadLock();
        try
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
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public SystemManifest Describe()
    {
        _lock.EnterReadLock();
        try
        {
            var operations = _operationMetadata
                .Select(op => new OperationInfo(
                    op.Name,
                    op.KeyType.Name,
                    op.ValueType.Name,
                    op.GetType().Name
                ))
                .OrderBy(op => op.DefinedBy)
                .ToList();
            return new SystemManifest(operations);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public void Dispose() => _lock.Dispose();
}
